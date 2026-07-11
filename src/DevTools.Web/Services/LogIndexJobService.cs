using System.Collections.Concurrent;
using DevTools.Shared.Models;

namespace DevTools.Web.Services;

public interface ILogIndexJobService
{
    LogRefreshStatus Start(int systemId, int? sourceId);
    LogRefreshStatus? Get(string jobId);
    LogRefreshStatus? GetLatestForSystem(int systemId);
    bool Cancel(string jobId);
}

public sealed class LogIndexJobService(IServiceScopeFactory scopeFactory, ILogIndexOperationCoordinator coordinator) : ILogIndexJobService
{
    private sealed class JobProgress(JobState job) : IProgress<LogRefreshProgress>
    {
        public void Report(LogRefreshProgress value) => job.Update(value);
    }

    private sealed class JobState
    {
        private readonly object _gate = new();
        public required string JobId { get; init; }
        public int SystemId { get; init; }
        public int? SourceId { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string Status { get; private set; } = "running";
        public int FilesDiscovered { get; private set; }
        public int FilesScanned { get; private set; }
        public int FilesIndexed { get; private set; }
        public int FilesUnchanged { get; private set; }
        public int FilesRemoved { get; private set; }
        public int FilesFailed { get; private set; }
        public long EntriesIndexed { get; private set; }
        public string? CurrentFile { get; private set; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public string? Error { get; private set; }

        public void Update(LogRefreshProgress progress)
        {
            lock (_gate)
            {
                FilesDiscovered = progress.FilesDiscovered;
                FilesScanned = progress.FilesScanned;
                FilesIndexed = progress.FilesIndexed;
                FilesUnchanged = progress.FilesUnchanged;
                FilesRemoved = progress.FilesRemoved;
                FilesFailed = progress.FilesFailed;
                EntriesIndexed = progress.EntriesIndexed;
                CurrentFile = progress.CurrentFile;
            }
        }

        public void Complete(LogRefreshResult result)
        {
            lock (_gate)
            {
                Status = "completed";
                FilesDiscovered = result.FilesDiscovered;
                FilesScanned = result.FilesDiscovered;
                FilesIndexed = result.FilesIndexed;
                FilesUnchanged = result.FilesUnchanged;
                FilesRemoved = result.FilesRemoved;
                FilesFailed = result.FilesFailed;
                EntriesIndexed = result.EntriesIndexed;
                CurrentFile = null;
                CompletedAt = result.RefreshedAt;
            }
        }

        public void Cancelled()
        {
            lock (_gate)
            {
                Status = "cancelled";
                CurrentFile = null;
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(Exception exception)
        {
            lock (_gate)
            {
                Status = "failed";
                Error = exception.Message;
                CurrentFile = null;
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public LogRefreshStatus Snapshot()
        {
            lock (_gate)
            {
                return new LogRefreshStatus(JobId, SystemId, SourceId, Status, FilesDiscovered, FilesScanned, FilesIndexed, FilesUnchanged, FilesRemoved, FilesFailed, EntriesIndexed, CurrentFile, StartedAt, CompletedAt, Error);
            }
        }
    }

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _activeSystemJobs = new();

    public LogRefreshStatus Start(int systemId, int? sourceId)
    {
        if (_activeSystemJobs.TryGetValue(systemId, out var existingJobId) && _jobs.TryGetValue(existingJobId, out var existing))
        {
            var snapshot = existing.Snapshot();
            if (snapshot.Status == "running") return snapshot;
            _activeSystemJobs.TryRemove(systemId, out _);
        }

        var job = new JobState { JobId = Guid.NewGuid().ToString("N"), SystemId = systemId, SourceId = sourceId };
        if (!coordinator.TryBegin(systemId, "indexing", job.JobId, out var activeOperation))
            throw new InvalidOperationException($"A {activeOperation} job is already running for this system.");
        _jobs[job.JobId] = job;
        _activeSystemJobs[systemId] = job.JobId;
        _ = Task.Run(() => RunJobAsync(job));
        return job.Snapshot();
    }

    public LogRefreshStatus? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job.Snapshot() : null;

    public LogRefreshStatus? GetLatestForSystem(int systemId)
    {
        if (_activeSystemJobs.TryGetValue(systemId, out var activeId) && _jobs.TryGetValue(activeId, out var active))
            return active.Snapshot();

        return _jobs.Values
            .Where(job => job.SystemId == systemId)
            .Select(job => job.Snapshot())
            .OrderByDescending(job => job.StartedAt)
            .FirstOrDefault();
    }

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        job.Cancellation.Cancel();
        return true;
    }

    private async Task RunJobAsync(JobState job)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ILogIndexService>();
            var progress = new JobProgress(job);
            var result = await service.RefreshAsync(job.SystemId, job.SourceId, progress, job.Cancellation.Token);
            job.Complete(result);
        }
        catch (OperationCanceledException) { job.Cancelled(); }
        catch (Exception exception) { job.Fail(exception); }
        finally
        {
            _activeSystemJobs.TryRemove(new KeyValuePair<int, string>(job.SystemId, job.JobId));
            coordinator.Complete(job.SystemId, "indexing", job.JobId);
        }
    }
}
