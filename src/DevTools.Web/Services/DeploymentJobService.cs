using System.Collections.Concurrent;
using System.Text.Json;
using DevTools.Application.Services.Deployment;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Services;

public interface IDeploymentJobRegistry
{
    bool IsBusy(int systemId);
}

public interface IDeploymentJobService : IDeploymentJobRegistry
{
    Task<DeploymentJobStatus> StartDeployAsync(int packageId, DeployPackageRequest request, string? actor, CancellationToken cancellationToken);
    Task<DeploymentJobStatus> StartRollbackAsync(int packageId, string? actor, CancellationToken cancellationToken);
    DeploymentJobStatus? Get(string jobId);
    DeploymentJobStatus? GetLatestForSystem(int systemId);
}

/// <summary>
/// Runs deployments and rollbacks as shared background jobs: one per system at a time, independent of the browser,
/// so leaving the page never interrupts a deployment and every viewer sees the same progress.
/// </summary>
public sealed class DeploymentJobService(
    IServiceScopeFactory scopeFactory,
    IDeploymentExecutor executor,
    IPackageArchiveService archives,
    IDeploymentPaths paths,
    IDeploymentVersionChecker versionChecker,
    ILogger<DeploymentJobService> logger) : IDeploymentJobService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> StepLabels = new Dictionary<string, string>
    {
        [DeploymentSteps.Prepare] = "Prepare package",
        [DeploymentSteps.Backup] = "Back up current files",
        [DeploymentSteps.Stop] = "Stop app pools and services",
        [DeploymentSteps.Patch] = "Patch files",
        [DeploymentSteps.Restore] = "Restore from backup",
        [DeploymentSteps.Start] = "Start app pools and services"
    };

    private sealed class Job(string jobId, int systemId, int packageId, string kind, IEnumerable<string> steps) : IDeploymentReporter
    {
        private readonly object _gate = new();
        private readonly List<(string Key, string Status, string? Detail)> _steps = steps.Select(key => (key, "pending", (string?)null)).ToList();
        private readonly List<DeploymentLogLine> _log = [];
        public string JobId { get; } = jobId;
        public int SystemId { get; } = systemId;
        public int PackageId { get; } = packageId;
        public string Kind { get; } = kind;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string Status { get; private set; } = "running";
        public DateTimeOffset? CompletedAt { get; private set; }
        public string? Error { get; private set; }
        public string? BackupFileName { get; set; }

        private static string FormatDuration(TimeSpan value) =>
            value.TotalSeconds < 60 ? $"{value.TotalSeconds:0.0} s" : $"{(int)value.TotalMinutes} min {value.Seconds} s";

        public void Step(string key, string status, string? detail = null)
        {
            lock (_gate)
            {
                var index = _steps.FindIndex(step => step.Key == key);
                // A restore step only appears when auto-restore runs; insert it before "start".
                if (index < 0)
                {
                    var startIndex = _steps.FindIndex(step => step.Key == DeploymentSteps.Start);
                    _steps.Insert(startIndex < 0 ? _steps.Count : startIndex, (key, status, detail));
                    return;
                }
                _steps[index] = (key, status, detail ?? (status == _steps[index].Status ? _steps[index].Detail : null));
            }
        }

        private readonly List<string> _summaries = [];
        public string? Actor { get; init; }

        public void Summary(string line)
        {
            lock (_gate) _summaries.Add(line);
        }

        /// <summary>The history detail for this job's outcome: who, how long, what changed, and where it failed.</summary>
        public string OutcomeDetail(string? error)
        {
            lock (_gate)
            {
                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(Actor)) lines.Add($"By: {Actor}");
                if (CompletedAt is { } completed) lines.Add($"Duration: {FormatDuration(completed - StartedAt)}");
                var failed = _steps.FirstOrDefault(step => step.Status == "failed");
                if (failed.Key is not null) lines.Add($"Failed at: {StepLabels.GetValueOrDefault(failed.Key, failed.Key)}");
                if (!string.IsNullOrWhiteSpace(error)) lines.Add($"Error: {error}");
                lines.AddRange(_summaries);
                return string.Join('\n', lines);
            }
        }

        public void FailRunningStep(string? detail)
        {
            lock (_gate)
            {
                var index = _steps.FindIndex(step => step.Status == "running");
                if (index >= 0) _steps[index] = (_steps[index].Key, "failed", detail);
            }
        }

        public void Log(string level, string message)
        {
            lock (_gate)
            {
                _log.Add(new DeploymentLogLine(DateTimeOffset.UtcNow, level, message));
                if (_log.Count > 1000) _log.RemoveAt(0);
            }
        }

        public void Finish(string status, string? error)
        {
            lock (_gate)
            {
                Status = status;
                Error = error;
                CompletedAt = DateTimeOffset.UtcNow;
                for (var index = 0; index < _steps.Count; index++)
                    if (_steps[index].Status is "pending" or "running") _steps[index] = (_steps[index].Key, status == "succeeded" ? "skipped" : "cancelled", _steps[index].Detail);
            }
        }

        public DeploymentJobStatus Snapshot()
        {
            lock (_gate)
            {
                return new DeploymentJobStatus(JobId, SystemId, PackageId, Kind, Status,
                    _steps.Select(step => new DeploymentStepDto(step.Key, StepLabels.GetValueOrDefault(step.Key, step.Key), step.Status, step.Detail)).ToList(),
                    _log.ToList(), StartedAt, CompletedAt, Error, BackupFileName);
            }
        }
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Job> _active = new();

    public bool IsBusy(int systemId) => _active.ContainsKey(systemId);

    public DeploymentJobStatus? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job.Snapshot() : null;

    public DeploymentJobStatus? GetLatestForSystem(int systemId) =>
        _active.TryGetValue(systemId, out var active)
            ? active.Snapshot()
            : _jobs.Values.Where(job => job.SystemId == systemId).OrderByDescending(job => job.StartedAt).FirstOrDefault()?.Snapshot();

    public async Task<DeploymentJobStatus> StartDeployAsync(int packageId, DeployPackageRequest request, string? actor, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
        var package = await database.DeploymentPackages.Include(item => item.DeploymentSystem!).ThenInclude(item => item.Modules)
            .FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken) ?? throw new KeyNotFoundException("Package not found.");
        var system = package.DeploymentSystem!;
        if (package.Status == DeploymentPackageStatuses.Deployed)
            throw new InvalidOperationException("This package is already deployed. Roll it back before deploying it again.");
        if (!File.Exists(package.StoredPath)) throw new InvalidOperationException("The uploaded package file is missing. Upload it again.");
        var analysis = JsonSerializer.Deserialize<DeploymentPackageAnalysis>(package.AnalysisJson, Json)!;

        // Validate the selection against the package and the system's current module settings.
        var selections = (request.Modules ?? []).GroupBy(item => item.ModuleId).Select(group => group.First()).ToList();
        if (selections.Count == 0) throw new ArgumentException("Choose at least one module to deploy.");
        var selected = new List<(DeploymentModule Module, DeploymentPackageModule Folder, string Mode, string? Exclusions)>();
        foreach (var selection in selections)
        {
            var module = system.Modules.FirstOrDefault(item => item.Id == selection.ModuleId) ?? throw new ArgumentException("A selected module no longer exists in this system.");
            var folder = analysis.Modules.FirstOrDefault(item => item.ModuleId == module.Id && item.FolderName.Equals(module.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The package has no '{module.Name}' folder. If the module was renamed, upload the package again.");
            if (!DeploymentModes.IsValid(selection.Mode)) throw new ArgumentException($"Choose partial or whole package for {module.Name}.");
            var exclusionsError = DeploymentPathMatcher.Validate(selection.Exclusions);
            if (exclusionsError is not null) throw new ArgumentException($"{module.Name}: {exclusionsError}");
            paths.EnsureSafeTargetFolder(module.TargetPath);
            selected.Add((module, folder, selection.Mode!, DeploymentPathMatcher.Normalize(selection.Exclusions)));
        }
        // The system can require every deployed module's main file to have a new version (not the same as on the server).
        var versionChecks = await Task.Run(() => versionChecker.Check(package.StoredPath, analysis.WrapperFolder,
            DeploymentVersionFile.Modules(system.Modules, analysis, selected.Select(item => item.Module.Id).ToList()), system.RequireVersionChange, cancellationToken), cancellationToken);
        var blocked = versionChecks.Where(check => check.Blocking).ToList();
        if (blocked.Count > 0) throw new ArgumentException(string.Join(" ", blocked.Select(check => $"{check.ModuleName}: {check.Message}")));
        var backupDirectory = paths.BackupDirectory(system);
        var overlapping = selected.FirstOrDefault(item => paths.Overlaps(item.Module.TargetPath, backupDirectory));
        if (overlapping.Module is not null) throw new ArgumentException($"The backup folder overlaps {overlapping.Module.Name}'s folder. Choose a backup folder outside the modules.");
        var packageOverlap = selected.FirstOrDefault(item => paths.Overlaps(item.Module.TargetPath, Path.GetDirectoryName(package.StoredPath) ?? package.StoredPath));
        if (packageOverlap.Module is not null) throw new ArgumentException($"The uploaded package is stored inside {packageOverlap.Module.Name}'s folder. Choose a package folder outside the modules and upload again.");

        var job = Register(system.Id, package.Id, "deploy", actor, [DeploymentSteps.Prepare, DeploymentSteps.Backup, DeploymentSteps.Stop, DeploymentSteps.Patch, DeploymentSteps.Start]);
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, DeploymentPackageLayout.BackupFileName(DateTime.Now, system.BackupFormat, name => DeploymentBackupFormats.Exists(Path.Combine(backupDirectory, name))));
            job.BackupFileName = Path.GetFileName(backupPath);
            // Recorded before any file changes, so an interrupted job can still be rolled back from its backup.
            package.Status = DeploymentPackageStatuses.Deploying;
            package.DeployedAtUtc = DateTime.UtcNow;
            package.RolledBackAtUtc = null;
            package.BackupPath = backupPath;
            package.ManifestJson = null;
            package.Error = null;
            package.Events.Add(new DeploymentEvent
            {
                Kind = DeploymentEventKinds.DeployStarted,
                AtUtc = package.DeployedAtUtc.Value,
                Detail = Truncate(string.Join('\n', new[]
                {
                    string.IsNullOrWhiteSpace(actor) ? null : $"By: {actor}",
                    $"Backup: {job.BackupFileName} in {backupDirectory}",
                    $"Auto-restore: {(request.AutoRestore ? "on" : "off")}"
                }.Where(line => line is not null).Concat(versionChecks.Select(check => (string?)$"{check.ModuleName} version: {check.Message}")).Concat(selected.Select(item =>
                    $"{item.Module.Name} — {(item.Mode == DeploymentModes.Full ? "whole package" : "partial update")} → {item.Module.TargetPath}"
                    + (item.Exclusions is null ? "" : $" · excluding {item.Exclusions.Replace("\n", ", ")}")
                    + (string.IsNullOrWhiteSpace(item.Module.AppPoolName) ? "" : $" · app pool {item.Module.AppPoolName}")
                    + (string.IsNullOrWhiteSpace(item.Module.ServiceName) ? "" : $" · service {item.Module.ServiceName}")))))
            });
            await database.SaveChangesAsync(cancellationToken);
            var plan = selected.Select(item => (item.Module, item.Folder, item.Mode, item.Exclusions)).ToList();
            _ = Task.Run(() => RunDeployAsync(job, package.StoredPath, package.FileName, analysis.WrapperFolder, backupPath, plan, request.AutoRestore));
        }
        catch
        {
            _active.TryRemove(new KeyValuePair<int, Job>(job.SystemId, job));
            throw;
        }
        job.Log("info", $"Deploying {package.FileName} to {selected.Count} module(s): {string.Join(", ", selected.Select(item => $"{item.Module.Name} ({(item.Mode == DeploymentModes.Full ? "whole package" : "partial")})"))}. Auto-restore {(request.AutoRestore ? "on" : "off")}.");
        return job.Snapshot();
    }

    public async Task<DeploymentJobStatus> StartRollbackAsync(int packageId, string? actor, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
        var package = await database.DeploymentPackages.FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken) ?? throw new KeyNotFoundException("Package not found.");
        var siblings = await database.DeploymentPackages.AsNoTracking().Where(item => item.DeploymentSystemId == package.DeploymentSystemId).ToListAsync(cancellationToken);
        var availability = DeploymentService.RollbackAvailability(siblings).GetValueOrDefault(package.Id);
        if (availability is not { CanRollback: true })
            throw new InvalidOperationException(availability?.Reason ?? "This package has no deployment in place to roll back.");

        var job = Register(package.DeploymentSystemId, package.Id, "rollback", actor, [DeploymentSteps.Stop, DeploymentSteps.Restore, DeploymentSteps.Start]);
        job.BackupFileName = Path.GetFileName(package.BackupPath);
        package.Status = DeploymentPackageStatuses.RollingBack;
        package.Events.Add(new DeploymentEvent { Kind = DeploymentEventKinds.RollbackStarted, AtUtc = DateTime.UtcNow, Detail = string.IsNullOrWhiteSpace(actor) ? $"Backup: {job.BackupFileName}" : $"By: {actor}\nBackup: {job.BackupFileName}" });
        await database.SaveChangesAsync(cancellationToken);
        _ = Task.Run(() => RunRollbackAsync(job, package.BackupPath!, package.ManifestJson));
        job.Log("info", $"Rolling back {package.FileName} using {job.BackupFileName}.");
        return job.Snapshot();
    }

    private Job Register(int systemId, int packageId, string kind, string? actor, IEnumerable<string> steps)
    {
        var job = new Job(Guid.NewGuid().ToString("N"), systemId, packageId, kind, steps) { Actor = actor };
        if (!_active.TryAdd(systemId, job)) throw new InvalidOperationException("A deployment or rollback is already running for this system.");
        _jobs[job.JobId] = job;
        return job;
    }

    private async Task RunDeployAsync(Job job, string packagePath, string packageFileName, string? wrapper,
        string backupPath, List<(DeploymentModule Module, DeploymentPackageModule Folder, string Mode, string? Exclusions)> selected, bool autoRestore)
    {
        var staging = Path.Combine(paths.StagingDirectory, job.JobId);
        string finalStatus = DeploymentPackageStatuses.Failed;
        string? error = null;
        string? manifestJson = null;
        try
        {
            // Extract only the selected module folders, in one pass over the archive.
            job.Step(DeploymentSteps.Prepare, "running", "Extracting package");
            var folders = selected.ToDictionary(item => item.Folder.FolderName, item => item.Module.Name, StringComparer.OrdinalIgnoreCase);
            var extracted = 0;
            await Task.Run(() => archives.Extract(packagePath, staging, entry =>
            {
                var located = DeploymentPackageLayout.Locate(entry, wrapper);
                return located is { } value && folders.TryGetValue(value.Folder, out var moduleName) ? $"{moduleName}/{value.RelativePath}" : null;
            }, _ => { if (++extracted % 250 == 0) job.Step(DeploymentSteps.Prepare, "running", $"{extracted:N0} files extracted"); }, CancellationToken.None));
            job.Step(DeploymentSteps.Prepare, "done", $"{extracted:N0} files extracted");

            var plan = new DeploymentPlan(job.PackageId, packageFileName, backupPath,
                selected.Select(item => new DeploymentModulePlan(item.Module.Id, item.Module.Name, item.Module.TargetPath, item.Mode, item.Exclusions,
                    item.Module.AppPoolName, item.Module.ServiceName, Path.Combine(staging, item.Module.Name))).ToList(),
                autoRestore);
            var manifest = await executor.DeployAsync(plan, job, CancellationToken.None);
            manifestJson = JsonSerializer.Serialize(manifest, Json);
            finalStatus = DeploymentPackageStatuses.Deployed;
            job.Log("info", "Deployment completed.");
            job.Finish("succeeded", null);
        }
        catch (DeploymentFailedException exception)
        {
            error = exception.Message;
            finalStatus = exception.Restored ? DeploymentPackageStatuses.Restored : DeploymentPackageStatuses.Failed;
            job.Finish(exception.Restored ? "restored" : "failed", exception.Message);
        }
        catch (Exception exception)
        {
            // Failures before the backup finished (extraction, backup) never changed the module folders.
            error = exception.Message;
            finalStatus = DeploymentPackageStatuses.Failed;
            job.FailRunningStep(exception.Message);
            job.Log("error", exception.Message);
            job.Finish("failed", exception.Message);
            logger.LogWarning("Deployment job {JobId} failed before files were changed.", job.JobId);
        }
        finally
        {
            TryDeleteDirectory(staging);
            var eventKind = finalStatus switch
            {
                DeploymentPackageStatuses.Deployed => DeploymentEventKinds.Deployed,
                DeploymentPackageStatuses.Restored => DeploymentEventKinds.Restored,
                _ => DeploymentEventKinds.DeployFailed
            };
            await SaveOutcomeAsync(job, eventKind, Truncate(job.OutcomeDetail(error)), package =>
            {
                package.Status = finalStatus;
                package.Error = Truncate(error);
                package.ManifestJson = manifestJson ?? ReadManifest(backupPath);
                // Nothing changed if no backup exists, or the deployment was restored; don't offer it for rollback.
                if (finalStatus == DeploymentPackageStatuses.Restored || !DeploymentBackupFormats.Exists(backupPath)) package.DeployedAtUtc = null;
                if (!DeploymentBackupFormats.Exists(backupPath)) package.BackupPath = null;
            });
        }
    }

    private async Task RunRollbackAsync(Job job, string backupPath, string? manifestJson)
    {
        string status = DeploymentPackageStatuses.Failed;
        string? error = null;
        try
        {
            var manifest = JsonSerializer.Deserialize<DeploymentManifest>(manifestJson ?? ReadManifest(backupPath) ?? throw new InvalidOperationException("The backup has no deployment manifest."), Json)
                ?? throw new InvalidOperationException("The deployment manifest could not be read.");
            await executor.RollbackAsync(backupPath, manifest, job, CancellationToken.None);
            status = DeploymentPackageStatuses.RolledBack;
            job.Log("info", "Rollback completed.");
            job.Finish("succeeded", null);
        }
        catch (Exception exception)
        {
            error = $"Rollback failed: {exception.Message}";
            job.FailRunningStep(exception.Message);
            job.Log("error", error);
            job.Finish("failed", error);
        }
        finally
        {
            var rolledBack = status == DeploymentPackageStatuses.RolledBack;
            await SaveOutcomeAsync(job, rolledBack ? DeploymentEventKinds.RolledBack : DeploymentEventKinds.RollbackFailed,
                Truncate(job.OutcomeDetail(rolledBack ? null : error)), package =>
            {
                package.Status = status;
                package.Error = Truncate(error);
                if (status == DeploymentPackageStatuses.RolledBack) package.RolledBackAtUtc = DateTime.UtcNow;
            });
        }
    }

    private async Task SaveOutcomeAsync(Job job, string eventKind, string? eventDetail, Action<DeploymentPackage> apply)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
            var package = await database.DeploymentPackages.FirstOrDefaultAsync(item => item.Id == job.PackageId);
            if (package is not null)
            {
                apply(package);
                database.DeploymentEvents.Add(new DeploymentEvent { DeploymentPackageId = package.Id, Kind = eventKind, AtUtc = DateTime.UtcNow, Detail = eventDetail });
                await database.SaveChangesAsync();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not record the outcome of deployment job {JobId}.", job.JobId);
        }
        finally
        {
            _active.TryRemove(new KeyValuePair<int, Job>(job.SystemId, job));
        }
    }

    private string? ReadManifest(string backupPath)
    {
        try { return DeploymentBackupFormats.Exists(backupPath) ? archives.ReadText(backupPath, DeploymentSteps.ManifestEntry, CancellationToken.None) : null; }
        catch (Exception) { return null; }
    }

    private static string? Truncate(string? value) => value is null || value.Length <= 2000 ? value : value[..2000];

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
