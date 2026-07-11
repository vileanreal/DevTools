using System.Collections.Concurrent;
using DevTools.Shared.Models;

namespace DevTools.Web.Services;

public interface ILogIndexClearJobService
{
    LogIndexClearStatus Start(int systemId, int? sourceId);
    LogIndexClearStatus? Get(string jobId);
    LogIndexClearStatus? GetLatestForSystem(int systemId);
}

public sealed class LogIndexClearJobService(IServiceScopeFactory scopeFactory, ILogIndexOperationCoordinator coordinator) : ILogIndexClearJobService
{
    private sealed class JobState
    {
        private readonly object _gate = new();
        public required string JobId { get; init; }
        public int SystemId { get; init; }
        public int? SourceId { get; init; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string Status { get; private set; } = "running";
        public long BytesBefore { get; private set; }
        public long BytesAfter { get; private set; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public string? Error { get; private set; }

        public void Complete(LogIndexClearResult result)
        {
            lock (_gate)
            {
                BytesBefore = result.BytesBefore;
                BytesAfter = result.BytesAfter;
                Status = "completed";
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(Exception exception)
        {
            lock (_gate)
            {
                Status = "failed";
                Error = exception.Message;
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public LogIndexClearStatus Snapshot()
        {
            lock (_gate) return new(JobId, SystemId, SourceId, Status, BytesBefore, BytesAfter, StartedAt, CompletedAt, Error);
        }
    }

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _activeSystemJobs = new();

    public LogIndexClearStatus Start(int systemId, int? sourceId)
    {
        if (_activeSystemJobs.TryGetValue(systemId, out var activeId) && _jobs.TryGetValue(activeId, out var active))
        {
            var current = active.Snapshot();
            if (current.Status == "running") return current;
            _activeSystemJobs.TryRemove(systemId, out _);
        }

        var job = new JobState { JobId = Guid.NewGuid().ToString("N"), SystemId = systemId, SourceId = sourceId };
        if (!coordinator.TryBegin(systemId, "clear-index", job.JobId, out var activeOperation))
            throw new InvalidOperationException($"A {activeOperation} job is already running for this system.");
        _jobs[job.JobId] = job;
        _activeSystemJobs[systemId] = job.JobId;
        _ = Task.Run(() => RunAsync(job));
        return job.Snapshot();
    }

    public LogIndexClearStatus? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job.Snapshot() : null;

    public LogIndexClearStatus? GetLatestForSystem(int systemId)
    {
        if (_activeSystemJobs.TryGetValue(systemId, out var activeId) && _jobs.TryGetValue(activeId, out var active)) return active.Snapshot();
        return _jobs.Values.Where(item => item.SystemId == systemId).Select(item => item.Snapshot()).OrderByDescending(item => item.StartedAt).FirstOrDefault();
    }

    private async Task RunAsync(JobState job)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ILogIndexService>();
            job.Complete(await service.ClearAsync(job.SystemId, job.SourceId, CancellationToken.None));
        }
        catch (Exception exception) { job.Fail(exception); }
        finally
        {
            _activeSystemJobs.TryRemove(new KeyValuePair<int, string>(job.SystemId, job.JobId));
            coordinator.Complete(job.SystemId, "clear-index", job.JobId);
        }
    }
}
