using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.ServiceProcess;
using System.Runtime.Versioning;
using DevTools.Shared.Models;
using TimeoutException = System.TimeoutException;

namespace DevTools.Application.Services;

public sealed class LocalSystemServiceRuntime : ISystemServiceRuntime
{
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ActionLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ServiceRuntimeStatus> GetStatusAsync(string kind, string targetName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Status("Unsupported", "Service monitoring is available only when DevTools runs on Windows.");

        try
        {
            return kind switch
            {
                "windows-service" => await WindowsServiceStatusAsync(targetName, cancellationToken),
                "app-pool" => await AppPoolCommandAsync(targetName, "list", cancellationToken),
                _ => Status("Unknown", "Unsupported service type.")
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or TimeoutException or IOException)
        {
            return Status("Unavailable", SafeMessage(exception));
        }
    }

    public async Task<ServiceRuntimeStatus> ExecuteAsync(string kind, string targetName, string action, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Service control is available only when DevTools runs on Windows.");

        var actionLock = ActionLocks.GetOrAdd($"{kind}:{targetName}", _ => new SemaphoreSlim(1, 1));
        await actionLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return kind switch
                {
                    "windows-service" => await ExecuteWindowsServiceAsync(targetName, action, cancellationToken),
                    "app-pool" => await ExecuteAppPoolAsync(targetName, action, cancellationToken),
                    _ => throw new InvalidOperationException("Unsupported service type.")
                };
            }
            catch (Exception exception) when (exception is Win32Exception or TimeoutException or IOException)
            {
                throw new InvalidOperationException(SafeMessage(exception));
            }
        }
        finally { actionLock.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static Task<ServiceRuntimeStatus> WindowsServiceStatusAsync(string targetName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var controller = new ServiceController(targetName);
            controller.Refresh();
            return Status(MapStatus(controller.Status), null);
        }, cancellationToken);

    [SupportedOSPlatform("windows")]
    private static async Task<ServiceRuntimeStatus> ExecuteWindowsServiceAsync(string targetName, string action, CancellationToken cancellationToken)
    {
        using var controller = new ServiceController(targetName);
        controller.Refresh();
        switch (action)
        {
            case "start":
                if (controller.Status != ServiceControllerStatus.Running) controller.Start();
                await WaitForWindowsStatusAsync(controller, ServiceControllerStatus.Running, cancellationToken);
                break;
            case "stop":
                if (!controller.CanStop) throw new InvalidOperationException("This Windows service does not accept stop commands.");
                if (controller.Status != ServiceControllerStatus.Stopped) controller.Stop();
                await WaitForWindowsStatusAsync(controller, ServiceControllerStatus.Stopped, cancellationToken);
                break;
            case "restart":
                if (!controller.CanStop) throw new InvalidOperationException("This Windows service does not accept restart commands.");
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    controller.Stop();
                    await WaitForWindowsStatusAsync(controller, ServiceControllerStatus.Stopped, cancellationToken);
                }
                controller.Start();
                await WaitForWindowsStatusAsync(controller, ServiceControllerStatus.Running, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unsupported service action.");
        }

        controller.Refresh();
        return Status(MapStatus(controller.Status), $"{FriendlyAction(action)} completed.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForWindowsStatusAsync(ServiceController controller, ServiceControllerStatus expected, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < ActionTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            controller.Refresh();
            if (controller.Status == expected) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"The service did not reach {MapStatus(expected)} within {ActionTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<ServiceRuntimeStatus> ExecuteAppPoolAsync(string targetName, string action, CancellationToken cancellationToken)
    {
        var command = action switch
        {
            "start" => "start",
            "stop" => "stop",
            "restart" => "recycle",
            _ => throw new InvalidOperationException("Unsupported service action.")
        };
        var result = await AppPoolCommandAsync(targetName, command, cancellationToken);
        if (result.Status == "Unavailable") throw new InvalidOperationException(result.Detail ?? "The IIS application pool command failed.");
        return await AppPoolCommandAsync(targetName, "list", cancellationToken);
    }

    private static async Task<ServiceRuntimeStatus> AppPoolCommandAsync(string targetName, string command, CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmd = Path.Combine(windows, "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appCmd)) return Status("Unavailable", "IIS appcmd.exe is not installed on this machine.");

        var startInfo = new ProcessStartInfo(appCmd)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("apppool");
        startInfo.ArgumentList.Add(targetName);
        if (command == "list") startInfo.ArgumentList.Add("/text:state");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start IIS appcmd.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ActionTimeout);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("The IIS application pool command timed out.");
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0) return Status("Unavailable", string.IsNullOrWhiteSpace(error) ? "The IIS application pool command failed." : error);
        if (command != "list") return Status("Changing", $"{FriendlyAction(command)} requested.");
        return Status(output.Equals("Started", StringComparison.OrdinalIgnoreCase) ? "Running" : output.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ? "Stopped" : string.IsNullOrWhiteSpace(output) ? "Unknown" : output, null);
    }

    [SupportedOSPlatform("windows")]
    private static string MapStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.StartPending => "Starting",
        ServiceControllerStatus.StopPending => "Stopping",
        ServiceControllerStatus.Paused => "Paused",
        ServiceControllerStatus.PausePending => "Pausing",
        ServiceControllerStatus.ContinuePending => "Resuming",
        _ => "Unknown"
    };

    private static ServiceRuntimeStatus Status(string value, string? detail) => new(value, detail, DateTimeOffset.UtcNow);
    private static string FriendlyAction(string action) => action switch { "start" => "Start", "stop" => "Stop", "restart" or "recycle" => "Restart", _ => "Action" };
    private static string SafeMessage(Exception exception) => exception.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        ? "Access was denied. Run DevTools with permission to control local services."
        : exception.Message;
}
