using System.Text.Json;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Application.Services.Deployment;

/// <summary>One module to deploy. <see cref="StagingPath"/> is the module's folder extracted from the package.</summary>
public sealed record DeploymentModulePlan(
    int ModuleId,
    string ModuleName,
    string TargetPath,
    string Mode,
    string? Exclusions,
    string? AppPoolName,
    string? ServiceName,
    string StagingPath);

public sealed record DeploymentPlan(int PackageId, string PackageFileName, string BackupPath, IReadOnlyList<DeploymentModulePlan> Modules, bool AutoRestore);

/// <summary>Receives progress. Step status: pending, running, done, failed, skipped.</summary>
public interface IDeploymentReporter
{
    void Step(string key, string status, string? detail = null);
    void Log(string level, string message);
    /// <summary>Marks whichever step is currently running as failed.</summary>
    void FailRunningStep(string? detail);
    /// <summary>A one-line result for the package history, such as what a module's patch changed.</summary>
    void Summary(string line);
}

public sealed class DeploymentFailedException(string message, bool restored, Exception? inner = null) : Exception(message, inner)
{
    public bool Restored { get; } = restored;
}

public interface IDeploymentExecutor
{
    /// <summary>Back up, stop every module's services, patch every module, then start the services again.</summary>
    Task<DeploymentManifest> DeployAsync(DeploymentPlan plan, IDeploymentReporter reporter, CancellationToken cancellationToken);

    /// <summary>Stop every module's services, restore every module from the backup, then start the services again.</summary>
    Task RollbackAsync(string backupPath, DeploymentManifest manifest, IDeploymentReporter reporter, CancellationToken cancellationToken);
}

public static class DeploymentSteps
{
    public const string Prepare = "prepare";
    public const string Backup = "backup";
    public const string Stop = "stop";
    public const string Patch = "patch";
    public const string Restore = "restore";
    public const string Start = "start";
    public const string ManifestEntry = "_deployment/manifest.json";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class DeploymentExecutor(IPackageArchiveService archives, ISystemServiceRuntime runtime) : IDeploymentExecutor
{
    private const int FileRetries = 6;
    private sealed record ServiceTarget(string Kind, string Name, string ModuleName)
    {
        public string Label => $"{(Kind == "app-pool" ? "app pool" : "service")} {Name}";
    }

    public async Task<DeploymentManifest> DeployAsync(DeploymentPlan plan, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        // 1. Work out what each module will change, then back it all up into one archive.
        reporter.Step(DeploymentSteps.Backup, "running", "Finding files that will be replaced");
        var modules = plan.Modules.Select(module => BuildManifestModule(module)).ToList();
        var manifest = new DeploymentManifest(plan.PackageId, plan.PackageFileName, DateTimeOffset.UtcNow, modules);
        var backupFiles = modules.SelectMany(module => module.BackedUpFiles.Select(file =>
            new ArchiveSource($"{module.ModuleName}/{file}", PackageArchiveService.ResolveInside(module.TargetPath, file)))).ToList();
        foreach (var module in modules)
            reporter.Log("info", $"{module.ModuleName}: {module.Mode} update, {module.BackedUpFiles.Count:N0} file(s) to back up, {module.AddedFiles.Count:N0} new file(s).");
        var backedUp = 0;
        await Task.Run(() => archives.CreateSevenZip(plan.BackupPath, backupFiles,
            new Dictionary<string, string> { [DeploymentSteps.ManifestEntry] = JsonSerializer.Serialize(manifest, DeploymentSteps.Json) },
            _ => { if (++backedUp % 200 == 0) reporter.Step(DeploymentSteps.Backup, "running", $"{backedUp:N0} of {backupFiles.Count:N0} files"); },
            cancellationToken), cancellationToken);
        reporter.Step(DeploymentSteps.Backup, "done", $"{backupFiles.Count:N0} file(s) in {Path.GetFileName(plan.BackupPath)}");
        reporter.Summary($"Backup: {Path.GetFileName(plan.BackupPath)} — {backupFiles.Count:N0} file(s), {FormatBytes(new FileInfo(plan.BackupPath).Length)}");
        reporter.Log("info", $"Backup written to {plan.BackupPath}.");

        // 2-4. Stop everything, patch everything, start everything.
        var targets = ServiceTargets(plan.Modules.Select(module => (module.ModuleName, module.AppPoolName, module.ServiceName)));
        var wasRunning = new List<ServiceTarget>();
        var patchStarted = false;
        try
        {
            wasRunning = await StopAllAsync(targets, reporter, cancellationToken);
            patchStarted = true;
            reporter.Step(DeploymentSteps.Patch, "running");
            foreach (var module in plan.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (copied, deleted, skipped) = await Task.Run(() => PatchModule(module, reporter, cancellationToken), cancellationToken);
                reporter.Log("info", $"{module.ModuleName}: {copied:N0} file(s) copied to {module.TargetPath}.");
                var added = manifest.Modules.First(item => item.ModuleId == module.ModuleId).AddedFiles.Count;
                var parts = new List<string> { $"{copied:N0} copied ({added:N0} new, {Math.Max(0, copied - added):N0} replaced)" };
                if (module.Mode == DeploymentModes.Full) parts.Add($"{deleted:N0} deleted");
                if (skipped > 0) parts.Add($"{skipped:N0} skipped by exclusions");
                reporter.Summary($"{module.ModuleName} — {(module.Mode == DeploymentModes.Full ? "whole package" : "partial update")}: {string.Join(", ", parts)}");
            }
            reporter.Step(DeploymentSteps.Patch, "done", $"{plan.Modules.Count} module(s) patched");
            await StartAllAsync(wasRunning, reporter, cancellationToken);
            return manifest;
        }
        catch (Exception exception) when (exception is not DeploymentFailedException)
        {
            var reason = exception is OperationCanceledException ? "The deployment was cancelled." : exception.Message;
            reporter.Log("error", reason);
            reporter.FailRunningStep(reason);
            if (!plan.AutoRestore)
            {
                reporter.Log("warn", "Auto-restore is off. Files and services were left as they are; use Rollback to restore the backup.");
                throw new DeploymentFailedException(reason, restored: false, exception);
            }
            if (!patchStarted)
            {
                // Nothing was changed on disk; only bring back services that were stopped.
                await TryStartAsync(wasRunning, reporter);
                throw new DeploymentFailedException(reason, restored: true, exception);
            }
            reporter.Log("warn", "Auto-restore: restoring every module from the backup.");
            try
            {
                reporter.Step(DeploymentSteps.Restore, "running");
                foreach (var module in manifest.Modules) await Task.Run(() => RestoreModule(plan.BackupPath, module, reporter, CancellationToken.None));
                reporter.Step(DeploymentSteps.Restore, "done", "Previous files restored");
                await StartAllAsync(wasRunning, reporter, CancellationToken.None);
            }
            catch (Exception restoreException)
            {
                reporter.Step(DeploymentSteps.Restore, "failed", restoreException.Message);
                reporter.Log("error", $"Auto-restore failed: {restoreException.Message}. Restore manually from {plan.BackupPath}.");
                throw new DeploymentFailedException($"{reason} Auto-restore also failed: {restoreException.Message}", restored: false, exception);
            }
            throw new DeploymentFailedException(reason, restored: true, exception);
        }
    }

    public async Task RollbackAsync(string backupPath, DeploymentManifest manifest, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath)) throw new InvalidOperationException($"The backup file {Path.GetFileName(backupPath)} no longer exists.");
        var targets = ServiceTargets(manifest.Modules.Select(module => (module.ModuleName, module.AppPoolName, module.ServiceName)));
        var wasRunning = await StopAllAsync(targets, reporter, cancellationToken);
        reporter.Step(DeploymentSteps.Restore, "running");
        foreach (var module in manifest.Modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => RestoreModule(backupPath, module, reporter, cancellationToken), cancellationToken);
        }
        reporter.Step(DeploymentSteps.Restore, "done", $"{manifest.Modules.Count} module(s) restored");
        await StartAllAsync(wasRunning, reporter, cancellationToken);
    }

    /// <summary>Decides which existing files are backed up and which package files are new.</summary>
    private static DeploymentManifestModule BuildManifestModule(DeploymentModulePlan module)
    {
        var exclusions = DeploymentPathMatcher.Parse(module.Exclusions);
        var packageFiles = RelativeFiles(module.StagingPath).Where(file => !exclusions.IsExcluded(file)).ToList();
        var targetExists = Directory.Exists(module.TargetPath);
        var existing = targetExists ? RelativeFiles(module.TargetPath).ToHashSet(PathComparer) : new HashSet<string>(PathComparer);
        var added = packageFiles.Where(file => !existing.Contains(file)).OrderBy(file => file, PathComparer).ToList();
        // Whole package: back up everything in the module folder. Partial: only the files that will be overwritten.
        var backedUp = module.Mode == DeploymentModes.Full
            ? existing.OrderBy(file => file, PathComparer).ToList()
            : packageFiles.Where(existing.Contains).OrderBy(file => file, PathComparer).ToList();
        return new DeploymentManifestModule(module.ModuleId, module.ModuleName, module.TargetPath, module.Mode, module.Exclusions, module.AppPoolName, module.ServiceName, backedUp, added);
    }

    private static (int Copied, int Deleted, int Skipped) PatchModule(DeploymentModulePlan module, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        var exclusions = DeploymentPathMatcher.Parse(module.Exclusions);
        Directory.CreateDirectory(module.TargetPath);
        var removed = 0;
        if (module.Mode == DeploymentModes.Full)
        {
            removed = ClearModule(module.TargetPath, exclusions, cancellationToken);
            reporter.Log("info", $"{module.ModuleName}: removed {removed:N0} existing file(s) (excluded files kept).");
        }
        var copied = 0;
        var skipped = 0;
        foreach (var file in RelativeFiles(module.StagingPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusions.IsExcluded(file)) { skipped++; continue; }
            var source = PackageArchiveService.ResolveInside(module.StagingPath, file);
            var destination = PackageArchiveService.ResolveInside(module.TargetPath, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Retry(() => File.Copy(source, destination, overwrite: true), destination, cancellationToken);
            if (++copied % 200 == 0) reporter.Step(DeploymentSteps.Patch, "running", $"{module.ModuleName}: {copied:N0} files copied");
        }
        return (copied, removed, skipped);
    }

    private void RestoreModule(string backupPath, DeploymentManifestModule module, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        var exclusions = DeploymentPathMatcher.Parse(module.Exclusions);
        Directory.CreateDirectory(module.TargetPath);
        if (module.Mode == DeploymentModes.Full)
        {
            ClearModule(module.TargetPath, exclusions, cancellationToken);
        }
        else
        {
            foreach (var file in module.AddedFiles)
            {
                var path = PackageArchiveService.ResolveInside(module.TargetPath, file);
                if (File.Exists(path)) Retry(() => File.Delete(path), path, cancellationToken);
            }
            RemoveEmptiedFolders(module.TargetPath, module.AddedFiles, exclusions);
        }
        var prefix = module.ModuleName + "/";
        // Excluded files were never touched, so they are not restored over newer copies.
        var restored = archives.Extract(backupPath, module.TargetPath, entry =>
        {
            var key = entry.Replace('\\', '/');
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = key[prefix.Length..];
            return exclusions.IsExcluded(relative) ? null : relative;
        }, null, cancellationToken);
        reporter.Log("info", $"{module.ModuleName}: restored {restored:N0} file(s), removed {module.AddedFiles.Count:N0} added file(s).");
        reporter.Summary($"{module.ModuleName} — restored {restored:N0} file(s), removed {module.AddedFiles.Count:N0} added file(s)");
    }

    /// <summary>Deletes every file that is not excluded, then removes folders left empty.</summary>
    private static int ClearModule(string targetPath, DeploymentPathMatcher exclusions, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var file in RelativeFiles(targetPath).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusions.IsExcluded(file)) continue;
            var path = PackageArchiveService.ResolveInside(targetPath, file);
            Retry(() => { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }, path, cancellationToken);
            removed.Add(file);
        }
        RemoveEmptiedFolders(targetPath, removed, exclusions);
        return removed.Count;
    }

    /// <summary>
    /// Removes folders that became empty because the given files were deleted. Folders that were already empty
    /// (an app may need an empty logs folder, for example) and excluded folders are left alone.
    /// </summary>
    private static void RemoveEmptiedFolders(string root, IEnumerable<string> deletedFiles, DeploymentPathMatcher exclusions)
    {
        var folders = new HashSet<string>(PathComparer);
        foreach (var file in deletedFiles)
            for (var folder = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar)); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                folders.Add(folder);
        foreach (var folder in folders.OrderByDescending(path => path.Length))
        {
            if (exclusions.IsExcluded(folder, isDirectory: true)) continue;
            var path = PackageArchiveService.ResolveInside(root, folder);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
    }

    private async Task<List<ServiceTarget>> StopAllAsync(IReadOnlyList<ServiceTarget> targets, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        var wasRunning = new List<ServiceTarget>();
        if (targets.Count == 0)
        {
            reporter.Step(DeploymentSteps.Stop, "skipped", "No app pools or services configured");
            return wasRunning;
        }
        reporter.Step(DeploymentSteps.Stop, "running");
        foreach (var target in targets)
        {
            var status = await runtime.GetStatusAsync(target.Kind, target.Name, cancellationToken);
            if (status.Status is "Unsupported" or "Unavailable" or "Unknown")
                throw new InvalidOperationException($"Cannot control {target.Label} ({target.ModuleName}): {status.Detail ?? status.Status}.");
            if (status.Status == "Stopped")
            {
                reporter.Log("info", $"{target.Label} was already stopped; it will stay stopped.");
                reporter.Summary($"{Capitalize(target.Label)} was already stopped (left stopped)");
                continue;
            }
            reporter.Step(DeploymentSteps.Stop, "running", $"Stopping {target.Label}");
            await runtime.ExecuteAsync(target.Kind, target.Name, "stop", cancellationToken);
            wasRunning.Add(target);
            reporter.Log("info", $"Stopped {target.Label}.");
            reporter.Summary($"Stopped {target.Label}");
        }
        reporter.Step(DeploymentSteps.Stop, "done", $"{wasRunning.Count} stopped");
        return wasRunning;
    }

    private async Task StartAllAsync(IReadOnlyList<ServiceTarget> targets, IDeploymentReporter reporter, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            reporter.Step(DeploymentSteps.Start, "skipped", "Nothing to start");
            return;
        }
        reporter.Step(DeploymentSteps.Start, "running");
        foreach (var target in targets)
        {
            reporter.Step(DeploymentSteps.Start, "running", $"Starting {target.Label}");
            await runtime.ExecuteAsync(target.Kind, target.Name, "start", cancellationToken);
            reporter.Log("info", $"Started {target.Label}.");
            reporter.Summary($"Started {target.Label}");
        }
        reporter.Step(DeploymentSteps.Start, "done", $"{targets.Count} started");
    }

    private async Task TryStartAsync(IReadOnlyList<ServiceTarget> targets, IDeploymentReporter reporter)
    {
        try { await StartAllAsync(targets, reporter, CancellationToken.None); }
        catch (Exception exception) { reporter.Log("error", $"Could not start services again: {exception.Message}"); }
    }

    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} B" : $"{size:0.#} {units[unit]}";
    }

    private static List<ServiceTarget> ServiceTargets(IEnumerable<(string ModuleName, string? AppPoolName, string? ServiceName)> modules)
    {
        var targets = new List<ServiceTarget>();
        foreach (var (moduleName, appPool, service) in modules)
        {
            if (!string.IsNullOrWhiteSpace(appPool)) targets.Add(new ServiceTarget("app-pool", appPool.Trim(), moduleName));
            if (!string.IsNullOrWhiteSpace(service)) targets.Add(new ServiceTarget("windows-service", service.Trim(), moduleName));
        }
        // Modules can share an app pool; control each target once.
        return targets.DistinctBy(target => $"{target.Kind}:{target.Name}", StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> RelativeFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            : [];

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Retries file operations briefly: a stopped IIS worker process can hold file locks for a few seconds.</summary>
    private static void Retry(Action action, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < FileRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * attempt));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not update {path}: {exception.Message}", exception);
            }
        }
    }
}
