using System.Collections.Concurrent;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Services.Provisioning;

/// <summary>
/// Picks queued <see cref="ClusterOperation"/>s from PostgreSQL and runs them. Operations are durable:
/// requests survive restarts, and operations interrupted by a restart are marked failed so they can be retried.
/// Designed for a single application instance.
/// </summary>
public class OperationWorker(
    IServiceScopeFactory scopeFactory,
    OperationCancellation cancellation,
    IOptions<ClusterDefaultsOptions> options,
    ILogger<OperationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The database may not be reachable yet (e.g. starting alongside PostgreSQL); keep trying rather than
        // taking the whole host down. Nothing is dispatched until recovery has succeeded.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverInterruptedAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Operation recovery failed, retrying in 10s");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Operation dispatch loop failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Give running operations a moment to record their state.
        await Task.WhenAny(Task.WhenAll(_running.Values), Task.Delay(TimeSpan.FromSeconds(20)));
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        var free = options.Value.MaxConcurrentOperations - _running.Count;
        if (free <= 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var busyClusters = await db.ClusterOperations
            .Where(o => o.Status == OperationStatus.Running)
            .Select(o => o.ClusterId).ToListAsync(ct);

        var candidates = await db.ClusterOperations
            .Where(o => o.Status == OperationStatus.Queued && !busyClusters.Contains(o.ClusterId))
            .OrderBy(o => o.CreatedAt)
            .Select(o => new { o.Id, o.ClusterId })
            .Take(free * 2)
            .ToListAsync(ct);

        var started = new HashSet<Guid>();
        foreach (var c in candidates)
        {
            if (_running.Count >= options.Value.MaxConcurrentOperations) break;
            if (!started.Add(c.ClusterId)) continue; // one operation per cluster at a time

            // Register before claiming so a cancel request that sees the operation as Running can always reach it.
            var cts = cancellation.Register(c.Id, ct);

            // Atomically claim the operation.
            var claimed = await db.ClusterOperations
                .Where(o => o.Id == c.Id && o.Status == OperationStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OperationStatus.Running)
                    .SetProperty(o => o.StartedAt, DateTimeOffset.UtcNow), ct);
            if (claimed == 0)
            {
                cancellation.Unregister(c.Id);
                continue;
            }

            _running[c.Id] = Task.Run(() => RunAsync(c.Id, cts.Token), CancellationToken.None);
        }
    }

    private async Task RunAsync(Guid operationId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var provisioner = scope.ServiceProvider.GetRequiredService<ClusterProvisioner>();
            await provisioner.ExecuteAsync(operationId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operation {Op} crashed", operationId);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.ClusterOperations.Where(o => o.Id == operationId && o.Status == OperationStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, OperationStatus.Failed)
                        .SetProperty(o => o.Error, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message)
                        .SetProperty(o => o.CompletedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
            catch (Exception inner)
            {
                logger.LogError(inner, "Could not record failure of operation {Op}", operationId);
            }
        }
        finally
        {
            _running.TryRemove(operationId, out _);
            cancellation.Unregister(operationId);
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interrupted = await db.ClusterOperations.Include(o => o.Cluster)
            .Where(o => o.Status == OperationStatus.Running).ToListAsync(ct);

        foreach (var op in interrupted)
        {
            op.Status = OperationStatus.Failed;
            op.Error = "Interrupted by an application restart. Use Retry to continue.";
            op.CompletedAt = DateTimeOffset.UtcNow;
            op.Log += $"{DateTimeOffset.UtcNow:HH:mm:ss} Interrupted by application restart\n";
            if (op.Cluster is not null) op.Cluster.Status = ClusterStatus.Failed;
        }

        if (interrupted.Count > 0)
        {
            logger.LogWarning("Marked {Count} interrupted operations as failed", interrupted.Count);
            await db.SaveChangesAsync(ct);
        }
    }
}
