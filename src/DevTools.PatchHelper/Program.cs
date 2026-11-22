using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DevTools.PatchHelper
{
    /// <summary>
    /// Starts the helper from the patch folder it sits in.
    ///   PatchHelper.exe                          the window
    ///   PatchHelper.exe /silent [/force] [/target:Module=D:\Folder]... [/rollback]
    ///                                            no window; writes apply-*.log next to the patch and returns an exit code:
    ///                                            0 applied (or nothing to do), 1 failed, 2 warnings found (use /force),
    ///                                            3 bad arguments or patch, 4 rolled back.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var patchFolder = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Any(arg => arg.Equals("/silent", StringComparison.OrdinalIgnoreCase))) return Silent(patchFolder, args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            PatchManifest manifest;
            try { manifest = PatchManifest.Load(patchFolder); }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
            {
                MessageBox.Show(exception.Message, "Patch Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }
            Application.Run(new MainForm(patchFolder, manifest));
            return 0;
        }

        private static int Silent(string patchFolder, string[] args)
        {
            var log = new FileLogReporter(Path.Combine(patchFolder, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            try
            {
                var manifest = PatchManifest.Load(patchFolder);
                var engine = new PatchEngine(patchFolder, manifest, new WindowsServiceControl(), log);
                var state = ApplyState.TryLoad(patchFolder);
                if (args.Any(arg => arg.Equals("/rollback", StringComparison.OrdinalIgnoreCase)))
                {
                    if (state == null) { log.Log("Nothing to roll back."); return 3; }
                    engine.Rollback(state, CancellationToken.None);
                    return 4;
                }

                var targets = new Dictionary<string, string>(TargetMemory.Load(manifest.System), StringComparer.OrdinalIgnoreCase);
                foreach (var module in manifest.Modules.Where(module => !targets.ContainsKey(module.Name) && !string.IsNullOrWhiteSpace(module.ClientFolder)))
                    targets[module.Name] = module.ClientFolder!;
                if (state != null && state.IsUnfinished) foreach (var target in state.Targets) targets[target.Key] = target.Value;
                foreach (var arg in args.Where(arg => arg.StartsWith("/target:", StringComparison.OrdinalIgnoreCase)))
                {
                    var pair = arg.Substring("/target:".Length).Split(new[] { '=' }, 2);
                    if (pair.Length != 2) { log.Log($"Bad argument {arg}; use /target:Module=D:\\Folder"); return 3; }
                    targets[pair[0]] = pair[1];
                }

                engine.CheckPatch(CancellationToken.None);
                var plan = engine.Compare(targets, CancellationToken.None);
                if (plan.Warnings.Any() && !args.Any(arg => arg.Equals("/force", StringComparison.OrdinalIgnoreCase)))
                {
                    log.Log("Stopped before changing anything: some files on this server aren't the version the patch was made from. Review the warnings above and run again with /force to apply anyway.");
                    return 2;
                }
                TargetMemory.Save(manifest.System, plan.Targets);
                if (plan.Files.All(file => file.Action == FileAction.Skip)) { log.Log("This server already has every file of this patch."); return 0; }
                engine.Apply(plan, state != null && state.IsUnfinished ? state : new ApplyState(), CancellationToken.None);
                return 0;
            }
            catch (Exception exception)
            {
                log.Log("Failed: " + exception.Message);
                return exception is PatchHelperException || exception is IOException || exception is UnauthorizedAccessException ? 1 : 3;
            }
        }
    }

    /// <summary>Writes progress to apply-*.log next to the patch (the window also shows it).</summary>
    public sealed class FileLogReporter : IHelperReporter
    {
        private readonly string _path;
        private readonly object _gate = new object();

        public FileLogReporter(string path) { _path = path; }

        public void Step(string key, StepStatus status, string? detail = null)
        {
            if (status == StepStatus.Running && detail != null && detail.Contains(" / ")) return; // per-file counters stay in the window
            Write($"[{key}] {status}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
        }

        public void Log(string message) => Write(message);
        public void Progress(int done, int total, string text) { }

        private void Write(string line)
        {
            lock (_gate)
            {
                try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Module folders chosen on this server, remembered per system in %ProgramData%.</summary>
    public static class TargetMemory
    {
        private static string PathFor(string system)
        {
            var safe = new string(system.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DevTools Patch Helper", safe + ".json");
        }

        public static Dictionary<string, string> Load(string system)
        {
            try
            {
                var path = PathFor(system);
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var saved = Json.Read<RememberedTargets>(File.ReadAllBytes(path));
                return new Dictionary<string, string>(saved?.Targets ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        public static void Save(string system, IDictionary<string, string> targets)
        {
            try
            {
                var path = PathFor(system);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var merged = Load(system);
                foreach (var target in targets) merged[target.Key] = target.Value;
                Json.WriteFile(path, new RememberedTargets { Targets = merged });
            }
            catch (Exception) { /* remembering folders is a convenience only */ }
        }
    }
}
