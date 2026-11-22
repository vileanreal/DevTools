using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DevTools.PatchHelper
{
    public enum StepStatus { Pending, Running, Done, Skipped, Failed }

    /// <summary>Stops and starts IIS app pools and Windows services. The Windows implementation uses appcmd and ServiceController.</summary>
    public interface IServiceControl
    {
        /// <summary>Stops the app pool and returns true when it was running (so it is started again afterwards).</summary>
        bool StopAppPool(string name);
        void StartAppPool(string name);
        /// <summary>Stops the service and returns true when it was running.</summary>
        bool StopService(string name);
        void StartService(string name);
    }

    /// <summary>Receives progress from the engine (the window, or the console log in silent mode).</summary>
    public interface IHelperReporter
    {
        void Step(string key, StepStatus status, string? detail = null);
        void Log(string message);
        void Progress(int done, int total, string text);
    }

    public sealed class PatchHelperException : Exception
    {
        public PatchHelperException(string message) : base(message) { }
    }

    public enum FileAction { Add, Replace, Delete, Skip }

    /// <summary>What the helper will do with one file of the patch on this server, and anything unexpected found there.</summary>
    public sealed class FilePlan
    {
        public string Module { get; set; } = "";
        public PatchManifestFile File { get; set; } = new PatchManifestFile();
        public string TargetPath { get; set; } = "";
        public string? SourcePath { get; set; }
        public FileAction Action { get; set; }
        /// <summary>Why a file is skipped, or what is unexpected about it.</summary>
        public string? Note { get; set; }
        /// <summary>True when the server's file isn't the version the patch was made from (edited or missing on the server).</summary>
        public bool Warning { get; set; }
    }

    public sealed class ApplyPlan
    {
        public List<FilePlan> Files { get; } = new List<FilePlan>();
        public Dictionary<string, string> Targets { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<FilePlan> Warnings => Files.Where(file => file.Warning);
        public int Count(FileAction action) => Files.Count(file => file.Action == action);
    }

    /// <summary>
    /// Applies a DevTools patch in fixed steps: check the patch, compare with the server, back up, stop app pools and
    /// services, apply each module, start what was running, and verify. Progress is saved after every step, so an
    /// interrupted run can continue or be rolled back.
    /// </summary>
    public sealed class PatchEngine
    {
        public const string StepCheck = "check";
        public const string StepCompare = "compare";
        public const string StepBackup = "backup";
        public const string StepStop = "stop";
        public const string StepStart = "start";
        public const string StepVerify = "verify";
        public const string StepRollback = "rollback";
        private const int CopyRetries = 5;

        private readonly string _patchFolder;
        private readonly IServiceControl _services;
        private readonly IHelperReporter _reporter;

        public PatchEngine(string patchFolder, PatchManifest manifest, IServiceControl services, IHelperReporter reporter)
        {
            _patchFolder = Path.GetFullPath(patchFolder);
            Manifest = manifest;
            _services = services;
            _reporter = reporter;
        }

        public PatchManifest Manifest { get; }

        public static string ApplyStep(string module) => "apply:" + module;

        /// <summary>The steps in order, for the window's step list.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Steps()
        {
            var steps = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(StepCheck, "Check the patch"),
                new KeyValuePair<string, string>(StepCompare, "Compare with this server"),
                new KeyValuePair<string, string>(StepBackup, "Back up the files that will change"),
                new KeyValuePair<string, string>(StepStop, "Stop app pools and services")
            };
            steps.AddRange(Manifest.Modules.Select(module => new KeyValuePair<string, string>(ApplyStep(module.Name), $"Apply {module.Name}")));
            steps.Add(new KeyValuePair<string, string>(StepStart, "Start app pools and services"));
            steps.Add(new KeyValuePair<string, string>(StepVerify, "Verify the applied files"));
            return steps;
        }

        // ---------- 1. Check the patch ----------

        /// <summary>Every file the patch should contain is present and has the hash recorded when it was compiled.</summary>
        public void CheckPatch(CancellationToken cancellationToken)
        {
            _reporter.Step(StepCheck, StepStatus.Running);
            var problems = new List<string>();
            var files = Manifest.Modules.SelectMany(module => module.Files.Where(file => file.Kind != PatchManifestFile.Deleted).Select(file => new { module, file })).ToList();
            var done = 0;
            foreach (var item in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SourcePath(item.module.Name, item.file);
                if (!File.Exists(source)) problems.Add($"{item.module.Name}/{item.file.Path}: missing from the patch");
                else if (item.file.Sha256 != null && !HashEquals(Sha256(source), item.file.Sha256)) problems.Add($"{item.module.Name}/{item.file.Path}: damaged (checksum differs)");
                _reporter.Progress(++done, files.Count, "Checking the patch files");
            }
            if (problems.Count > 0)
            {
                _reporter.Step(StepCheck, StepStatus.Failed, $"{problems.Count} problem(s)");
                foreach (var problem in problems.Take(20)) _reporter.Log("  " + problem);
                throw new PatchHelperException($"The patch is incomplete or damaged ({problems.Count} file(s)). Extract it again or ask for a new copy.");
            }
            _reporter.Step(StepCheck, StepStatus.Done, $"{files.Count} file(s), checksums OK");
            _reporter.Log($"Patch checked: {files.Count} file(s) to copy, {Manifest.Modules.Sum(module => module.Files.Count(file => file.Kind == PatchManifestFile.Deleted))} to delete.");
        }

        // ---------- 2. Compare with the server ----------

        /// <summary>
        /// Works out what each file needs on this server. A file already at the patch's version is skipped; a file that
        /// isn't the version the patch was made from (edited by hand, or missing) is a warning the user must accept.
        /// </summary>
        public ApplyPlan Compare(IDictionary<string, string> targets, CancellationToken cancellationToken)
        {
            _reporter.Step(StepCompare, StepStatus.Running);
            var plan = new ApplyPlan();
            foreach (var module in Manifest.Modules)
            {
                if (!targets.TryGetValue(module.Name, out var folder) || string.IsNullOrWhiteSpace(folder))
                    throw Fail(StepCompare, $"Choose the folder of {module.Name} on this server.");
                folder = Path.GetFullPath(folder.Trim());
                if (!Directory.Exists(folder)) throw Fail(StepCompare, $"The folder for {module.Name} doesn't exist: {folder}");
                if (Path.GetPathRoot(folder)!.TrimEnd('\\', '/').Length == folder.TrimEnd('\\', '/').Length)
                    throw Fail(StepCompare, $"{module.Name}'s folder can't be a drive root: {folder}");
                plan.Targets[module.Name] = folder;
            }

            var all = Manifest.Modules.SelectMany(module => module.Files.Select(file => new { module, file })).ToList();
            var done = 0;
            foreach (var item in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveInside(plan.Targets[item.module.Name], item.file.Path);
                var exists = File.Exists(target);
                var current = exists ? Sha256(target) : null;
                var entry = new FilePlan { Module = item.module.Name, File = item.file, TargetPath = target, SourcePath = item.file.Kind == PatchManifestFile.Deleted ? null : SourcePath(item.module.Name, item.file) };
                switch (item.file.Kind)
                {
                    case PatchManifestFile.Added:
                        if (!exists) entry.Action = FileAction.Add;
                        else if (HashEquals(current, item.file.Sha256)) { entry.Action = FileAction.Skip; entry.Note = "already at the patch version"; }
                        else { entry.Action = FileAction.Replace; entry.Warning = true; entry.Note = "a different file with this name is already on the server; it will be replaced"; }
                        break;
                    case PatchManifestFile.Changed:
                        if (!exists) { entry.Action = FileAction.Add; entry.Warning = true; entry.Note = "missing on the server; it will be added"; }
                        else if (HashEquals(current, item.file.Sha256)) { entry.Action = FileAction.Skip; entry.Note = "already at the patch version"; }
                        else if (HashEquals(current, item.file.PreviousSha256)) entry.Action = FileAction.Replace;
                        else { entry.Action = FileAction.Replace; entry.Warning = true; entry.Note = "edited on the server (not the version the patch was made from); it will be replaced"; }
                        break;
                    case PatchManifestFile.Deleted:
                        if (!exists) { entry.Action = FileAction.Skip; entry.Note = "already deleted"; }
                        else if (item.file.PreviousSha256 == null || HashEquals(current, item.file.PreviousSha256)) entry.Action = FileAction.Delete;
                        else { entry.Action = FileAction.Delete; entry.Warning = true; entry.Note = "edited on the server; it will still be deleted"; }
                        break;
                    default:
                        throw Fail(StepCompare, $"Unknown change type '{item.file.Kind}' for {item.module.Name}/{item.file.Path}.");
                }
                plan.Files.Add(entry);
                _reporter.Progress(++done, all.Count, "Comparing with this server");
            }

            var summary = $"{plan.Count(FileAction.Add)} to add, {plan.Count(FileAction.Replace)} to replace, {plan.Count(FileAction.Delete)} to delete, {plan.Count(FileAction.Skip)} already done";
            var warnings = plan.Warnings.Count();
            _reporter.Step(StepCompare, StepStatus.Done, warnings > 0 ? $"{summary} · {warnings} warning(s)" : summary);
            _reporter.Log($"Compared with this server: {summary}.");
            foreach (var warning in plan.Warnings) _reporter.Log($"  Warning: {warning.Module}/{warning.File.Path} — {warning.Note}");
            return plan;
        }

        // ---------- 3-7. Back up, stop, apply, start, verify ----------

        /// <summary>
        /// Runs the remaining steps. Steps already in <paramref name="state"/>.CompletedSteps are skipped, so a run that
        /// was interrupted continues where it stopped. The state is saved after every step.
        /// </summary>
        public void Apply(ApplyPlan plan, ApplyState state, CancellationToken cancellationToken)
        {
            state.Status = "applying";
            foreach (var target in plan.Targets) state.Targets[target.Key] = target.Value;
            if (string.IsNullOrEmpty(state.BackupFolder)) state.BackupFolder = NewBackupFolder();
            if (string.IsNullOrEmpty(state.StartedAt)) state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            state.Save(_patchFolder);
            var current = "";
            try
            {
                current = StepBackup;
                if (!Completed(state, StepBackup)) { Backup(plan, state, cancellationToken); Complete(state, StepBackup); }
                else _reporter.Step(StepBackup, StepStatus.Done, "done earlier");

                current = StepStop;
                if (!Completed(state, StepStop)) { StopAll(state); Complete(state, StepStop); }
                else _reporter.Step(StepStop, StepStatus.Done, "done earlier");

                foreach (var module in Manifest.Modules)
                {
                    current = ApplyStep(module.Name);
                    if (Completed(state, current)) { _reporter.Step(current, StepStatus.Done, "done earlier"); continue; }
                    ApplyModule(module.Name, plan, cancellationToken);
                    Complete(state, current);
                }

                current = StepStart;
                StartStopped(state, StepStart);
                Complete(state, StepStart);

                current = StepVerify;
                Verify(plan, cancellationToken);
                Complete(state, StepVerify);
                state.Status = "applied";
                state.Save(_patchFolder);
                _reporter.Log("The patch was applied and verified.");
            }
            catch (Exception exception)
            {
                state.Status = "failed";
                state.Save(_patchFolder);
                _reporter.Step(current, StepStatus.Failed, exception is OperationCanceledException ? "cancelled" : exception.Message);
                _reporter.Log($"Failed at “{current}”: {(exception is OperationCanceledException ? "cancelled" : exception.Message)}");
                throw;
            }
        }

        private void Backup(ApplyPlan plan, ApplyState state, CancellationToken cancellationToken)
        {
            _reporter.Step(StepBackup, StepStatus.Running);
            var touched = plan.Files.Where(file => file.Action != FileAction.Skip).ToList();
            var index = new BackupIndex();
            Directory.CreateDirectory(state.BackupFolder);
            var done = 0;
            foreach (var file in touched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existed = File.Exists(file.TargetPath);
                if (existed)
                {
                    var copy = ResolveInside(Path.Combine(state.BackupFolder, SafeFolderName(file.Module)), file.File.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file.TargetPath, copy, overwrite: true);
                }
                index.Entries.Add(new BackupEntry { Module = file.Module, Path = file.File.Path, Existed = existed });
                _reporter.Progress(++done, touched.Count, "Backing up");
            }
            Json.WriteFile(Path.Combine(state.BackupFolder, BackupIndex.FileName), index);
            var copied = index.Entries.Count(entry => entry.Existed);
            _reporter.Step(StepBackup, StepStatus.Done, $"{copied} file(s) → {Path.GetFileName(state.BackupFolder)}");
            _reporter.Log($"Backed up {copied} file(s) to {state.BackupFolder}.");
        }

        private void StopAll(ApplyState state)
        {
            var pools = Manifest.Modules.Select(module => module.AppPool).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var services = Manifest.Modules.Select(module => module.Service).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (pools.Count == 0 && services.Count == 0) { _reporter.Step(StepStop, StepStatus.Skipped, "no app pools or services configured"); return; }
            _reporter.Step(StepStop, StepStatus.Running);
            foreach (var pool in pools.Where(pool => !state.StoppedAppPools.Contains(pool, StringComparer.OrdinalIgnoreCase)))
            {
                _reporter.Step(StepStop, StepStatus.Running, $"app pool {pool}");
                if (_services.StopAppPool(pool)) { state.StoppedAppPools.Add(pool); state.Save(_patchFolder); _reporter.Log($"Stopped app pool {pool}."); }
                else _reporter.Log($"App pool {pool} was already stopped; it will stay stopped.");
            }
            foreach (var service in services.Where(service => !state.StoppedServices.Contains(service, StringComparer.OrdinalIgnoreCase)))
            {
                _reporter.Step(StepStop, StepStatus.Running, $"service {service}");
                if (_services.StopService(service)) { state.StoppedServices.Add(service); state.Save(_patchFolder); _reporter.Log($"Stopped service {service}."); }
                else _reporter.Log($"Service {service} was already stopped; it will stay stopped.");
            }
            _reporter.Step(StepStop, StepStatus.Done, $"{state.StoppedAppPools.Count + state.StoppedServices.Count} stopped");
        }

        private void ApplyModule(string module, ApplyPlan plan, CancellationToken cancellationToken)
        {
            var step = ApplyStep(module);
            var files = plan.Files.Where(file => file.Module == module).ToList();
            var work = files.Where(file => file.Action != FileAction.Skip).ToList();
            _reporter.Step(step, StepStatus.Running, $"0 / {work.Count}");
            var done = 0;
            foreach (var file in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Retry(() =>
                {
                    if (file.Action == FileAction.Delete)
                    {
                        if (File.Exists(file.TargetPath)) { File.SetAttributes(file.TargetPath, FileAttributes.Normal); File.Delete(file.TargetPath); }
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath)!);
                        if (File.Exists(file.TargetPath)) File.SetAttributes(file.TargetPath, FileAttributes.Normal);
                        File.Copy(file.SourcePath!, file.TargetPath, overwrite: true);
                    }
                }, file.TargetPath, cancellationToken);
                _reporter.Log($"  {(file.Action == FileAction.Delete ? "Deleted" : file.Action == FileAction.Add ? "Added" : "Replaced")} {module}/{file.File.Path}");
                done++;
                _reporter.Step(step, StepStatus.Running, $"{done} / {work.Count}");
                _reporter.Progress(done, work.Count, $"Applying {module}");
            }
            var skipped = files.Count - work.Count;
            _reporter.Step(step, StepStatus.Done, $"{work.Count(file => file.Action == FileAction.Add)} added, {work.Count(file => file.Action == FileAction.Replace)} replaced, {work.Count(file => file.Action == FileAction.Delete)} deleted{(skipped > 0 ? $", {skipped} already done" : "")}");
        }

        private void StartStopped(ApplyState state, string step)
        {
            if (state.StoppedAppPools.Count == 0 && state.StoppedServices.Count == 0) { _reporter.Step(step, StepStatus.Skipped, "nothing was stopped"); return; }
            _reporter.Step(step, StepStatus.Running);
            foreach (var service in state.StoppedServices.ToList())
            {
                _reporter.Step(step, StepStatus.Running, $"service {service}");
                _services.StartService(service);
                _reporter.Log($"Started service {service}.");
            }
            foreach (var pool in state.StoppedAppPools.ToList())
            {
                _reporter.Step(step, StepStatus.Running, $"app pool {pool}");
                _services.StartAppPool(pool);
                _reporter.Log($"Started app pool {pool}.");
            }
            var count = state.StoppedAppPools.Count + state.StoppedServices.Count;
            state.StoppedAppPools.Clear();
            state.StoppedServices.Clear();
            state.Save(_patchFolder);
            _reporter.Step(step, StepStatus.Done, $"{count} started");
        }

        private void Verify(ApplyPlan plan, CancellationToken cancellationToken)
        {
            _reporter.Step(StepVerify, StepStatus.Running);
            var problems = new List<string>();
            var done = 0;
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.File.Kind == PatchManifestFile.Deleted) { if (File.Exists(file.TargetPath)) problems.Add($"{file.Module}/{file.File.Path} still exists"); }
                else if (!File.Exists(file.TargetPath)) problems.Add($"{file.Module}/{file.File.Path} is missing");
                else if (file.File.Sha256 != null && !HashEquals(Sha256(file.TargetPath), file.File.Sha256)) problems.Add($"{file.Module}/{file.File.Path} differs from the patch");
                _reporter.Progress(++done, plan.Files.Count, "Verifying");
            }
            if (problems.Count > 0)
            {
                foreach (var problem in problems.Take(20)) _reporter.Log("  " + problem);
                throw new PatchHelperException($"{problems.Count} file(s) don't match the patch after applying.");
            }
            _reporter.Step(StepVerify, StepStatus.Done, $"{plan.Files.Count} file(s) match the patch");
        }

        // ---------- Roll back ----------

        /// <summary>
        /// Restores every backed-up file, removes files the patch added, and starts what was running. Services are stopped
        /// first (if the apply had already started them again) so files aren't replaced while in use.
        /// </summary>
        public void Rollback(ApplyState state, CancellationToken cancellationToken)
        {
            _reporter.Step(StepRollback, StepStatus.Running);
            var indexPath = Path.Combine(state.BackupFolder, BackupIndex.FileName);
            if (!File.Exists(indexPath))
            {
                // The backup step never finished, so no file was changed; only bring back anything that was stopped.
                StartStopped(state, StepRollback);
                state.Status = "rolled-back";
                state.Save(_patchFolder);
                _reporter.Step(StepRollback, StepStatus.Done, "nothing had been changed");
                return;
            }
            var index = Json.Read<BackupIndex>(File.ReadAllBytes(indexPath)) ?? new BackupIndex();
            if (state.StoppedAppPools.Count == 0 && state.StoppedServices.Count == 0) StopAll(state);
            var done = 0;
            foreach (var entry in index.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Targets.TryGetValue(entry.Module, out var folder)) continue;
                var target = ResolveInside(folder, entry.Path);
                Retry(() =>
                {
                    if (entry.Existed)
                    {
                        var copy = ResolveInside(Path.Combine(state.BackupFolder, SafeFolderName(entry.Module)), entry.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                        File.Copy(copy, target, overwrite: true);
                    }
                    else if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                }, target, cancellationToken);
                _reporter.Progress(++done, index.Entries.Count, "Restoring");
            }
            _reporter.Log($"Restored {index.Entries.Count(entry => entry.Existed)} file(s) and removed {index.Entries.Count(entry => !entry.Existed)} added file(s).");
            StartStopped(state, StepStart);
            state.Status = "rolled-back";
            state.Save(_patchFolder);
            _reporter.Step(StepRollback, StepStatus.Done, $"{index.Entries.Count} file(s) restored");
        }

        // ---------- Helpers ----------

        public string SourcePath(string module, PatchManifestFile file) =>
            ResolveInside(Path.Combine(_patchFolder, SafeFolderName(module), file.Kind), file.Path);

        private string NewBackupFolder()
        {
            var folder = Path.Combine(_patchFolder, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            for (var counter = 2; Directory.Exists(folder); counter++) folder = Path.Combine(_patchFolder, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}-{counter}");
            return folder;
        }

        private bool Completed(ApplyState state, string step) => state.CompletedSteps.Contains(step);

        private void Complete(ApplyState state, string step)
        {
            if (!state.CompletedSteps.Contains(step)) state.CompletedSteps.Add(step);
            state.Save(_patchFolder);
        }

        private PatchHelperException Fail(string step, string message)
        {
            _reporter.Step(step, StepStatus.Failed, message);
            return new PatchHelperException(message);
        }

        /// <summary>A locked file (for example still in use by w3wp.exe) is retried a few times before giving up.</summary>
        private void Retry(Action action, string path, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (Exception exception) when ((exception is IOException || exception is UnauthorizedAccessException) && attempt < CopyRetries)
                {
                    _reporter.Log($"  {Path.GetFileName(path)} is in use, retrying ({attempt}/{CopyRetries - 1})…");
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(attempt));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (IOException exception) { throw new PatchHelperException($"{path}: {exception.Message}"); }
                catch (UnauthorizedAccessException) { throw new PatchHelperException($"Access to {path} was denied. Run the helper as administrator, and check that no program has the file open."); }
            }
        }

        /// <summary>Resolves a patch path under a root and refuses anything that would land outside it.</summary>
        public static string ResolveInside(string root, string relative)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleaned = relative.Replace('\\', '/').TrimStart('/');
            if (cleaned.Length == 0 || cleaned.Split('/').Any(part => part == ".." || part.Contains(":")))
                throw new PatchHelperException($"Unsafe path in the patch: {relative}");
            var full = Path.GetFullPath(Path.Combine(rootFull, cleaned.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new PatchHelperException($"Unsafe path in the patch: {relative}");
            return full;
        }

        private static string SafeFolderName(string module)
        {
            if (module.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || module == "." || module == "..") throw new PatchHelperException($"Unsafe module name in the patch: {module}");
            return module;
        }

        public static string Sha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var text = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static bool HashEquals(string? first, string? second) =>
            first != null && second != null && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }
}
