using System.Collections.Concurrent;

namespace K3sProxmox.Web.Services.Provisioning;

/// <summary>
/// Tracks the cancellation sources of operations running in this process so a user request can stop one.
/// Singleton; the application is designed for a single instance.
/// </summary>
public class OperationCancellation
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelledByUser = new();

    public CancellationTokenSource Register(Guid operationId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _sources[operationId] = cts;
        return cts;
    }

    public void Unregister(Guid operationId)
    {
        if (_sources.TryRemove(operationId, out var cts)) cts.Dispose();
        _cancelledByUser.TryRemove(operationId, out _);
    }

    /// <summary>Signals the operation to stop. Returns false if it is not running in this process.</summary>
    public bool Cancel(Guid operationId)
    {
        if (!_sources.TryGetValue(operationId, out var cts)) return false;
        _cancelledByUser[operationId] = 0;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public bool WasCancelledByUser(Guid operationId) => _cancelledByUser.ContainsKey(operationId);
}
