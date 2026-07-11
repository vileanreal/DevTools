using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public interface ISystemServiceRuntime
{
    Task<ServiceRuntimeStatus> GetStatusAsync(string kind, string targetName, CancellationToken cancellationToken);
    Task<ServiceRuntimeStatus> ExecuteAsync(string kind, string targetName, string action, CancellationToken cancellationToken);
}
