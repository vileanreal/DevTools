using System.Collections.Concurrent;

namespace DevTools.Web.Services;

public interface ILogIndexOperationCoordinator
{
    bool TryBegin(int systemId, string operation, string jobId, out string? activeOperation);
    void Complete(int systemId, string operation, string jobId);
}

public sealed class LogIndexOperationCoordinator : ILogIndexOperationCoordinator
{
    private sealed record Operation(string Name, string JobId);
    private readonly ConcurrentDictionary<int, Operation> _active = new();

    public bool TryBegin(int systemId, string operation, string jobId, out string? activeOperation)
    {
        if (_active.TryAdd(systemId, new Operation(operation, jobId)))
        {
            activeOperation = null;
            return true;
        }
        activeOperation = _active.TryGetValue(systemId, out var current) ? current.Name : "maintenance";
        return false;
    }

    public void Complete(int systemId, string operation, string jobId) =>
        _active.TryRemove(new KeyValuePair<int, Operation>(systemId, new Operation(operation, jobId)));
}
