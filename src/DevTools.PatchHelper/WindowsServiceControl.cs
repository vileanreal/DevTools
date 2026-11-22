using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace DevTools.PatchHelper
{
    /// <summary>IIS app pools through Windows' own appcmd.exe (no extra DLL needed) and services through ServiceController.</summary>
    public sealed class WindowsServiceControl : IServiceControl
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        private static string AppCmd
        {
            get
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
                if (!File.Exists(path)) throw new PatchHelperException("IIS isn't installed on this server (appcmd.exe was not found), so app pools can't be stopped. Remove the app pool from the module in DevTools, or install IIS.");
                return path;
            }
        }

        public bool StopAppPool(string name)
        {
            var state = RunAppCmd($"list apppool /name:\"{name}\" /text:state").Trim();
            if (state.Length == 0) throw new PatchHelperException($"The app pool “{name}” doesn't exist on this server.");
            if (!state.Equals("Started", StringComparison.OrdinalIgnoreCase)) return false;
            RunAppCmd($"stop apppool /apppool.name:\"{name}\"");
            WaitForAppPool(name, "Stopped");
            return true;
        }

        public void StartAppPool(string name)
        {
            RunAppCmd($"start apppool /apppool.name:\"{name}\"");
            WaitForAppPool(name, "Started");
        }

        public bool StopService(string name)
        {
            using (var service = Find(name))
            {
                if (service.Status == ServiceControllerStatus.Stopped || service.Status == ServiceControllerStatus.StopPending && Wait(service, ServiceControllerStatus.Stopped)) return false;
                service.Stop();
                if (!Wait(service, ServiceControllerStatus.Stopped)) throw new PatchHelperException($"The service “{name}” didn't stop within {Timeout.TotalSeconds:N0} seconds.");
                return true;
            }
        }

        public void StartService(string name)
        {
            using (var service = Find(name))
            {
                if (service.Status == ServiceControllerStatus.Running) return;
                service.Start();
                if (!Wait(service, ServiceControllerStatus.Running)) throw new PatchHelperException($"The service “{name}” didn't start within {Timeout.TotalSeconds:N0} seconds. Check the Windows event log.");
            }
        }

        private static ServiceController Find(string name)
        {
            var service = new ServiceController(name);
            try { _ = service.Status; return service; }
            catch (InvalidOperationException) { service.Dispose(); throw new PatchHelperException($"The Windows service “{name}” doesn't exist on this server."); }
        }

        private static bool Wait(ServiceController service, ServiceControllerStatus status)
        {
            try { service.WaitForStatus(status, Timeout); return true; }
            catch (System.ServiceProcess.TimeoutException) { return false; }
        }

        private static void WaitForAppPool(string name, string expected)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < Timeout)
            {
                if (RunAppCmd($"list apppool /name:\"{name}\" /text:state").Trim().Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
                System.Threading.Thread.Sleep(500);
            }
            throw new PatchHelperException($"The app pool “{name}” didn't become {expected.ToLowerInvariant()} within {Timeout.TotalSeconds:N0} seconds.");
        }

        private static string RunAppCmd(string arguments)
        {
            var start = new ProcessStartInfo(AppCmd, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(start)!)
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                // "list" with no match exits 1 and prints nothing; stop/start failures print an ERROR line.
                if (process.ExitCode != 0 && (output + error).IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new PatchHelperException($"appcmd {arguments.Split(' ')[0]} failed: {(output + error).Trim()}");
                return output;
            }
        }
    }
}
