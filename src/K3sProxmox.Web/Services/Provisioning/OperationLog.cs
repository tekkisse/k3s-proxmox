using System.Collections.Concurrent;
using System.Text;
using K3sProxmox.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace K3sProxmox.Web.Services.Provisioning;

/// <summary>
/// Thread-safe operation log. Lines are buffered and appended to the operation row in the database
/// every couple of seconds so the UI can show progress while long steps run.
/// </summary>
public sealed class OperationLog : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Guid _operationId;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public OperationLog(IDbContextFactory<AppDbContext> dbFactory, Guid operationId, ILogger logger)
    {
        _dbFactory = dbFactory;
        _operationId = operationId;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public void Write(string message)
    {
        _logger.LogInformation("[op {Op}] {Message}", _operationId, message);
        _pending.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {message}\n");
    }

    public async Task FlushAsync()
    {
        if (_pending.IsEmpty) return;
        await _flushLock.WaitAsync();
        try
        {
            var sb = new StringBuilder();
            while (_pending.TryDequeue(out var line)) sb.Append(line);
            if (sb.Length == 0) return;
            var text = sb.ToString();
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.ClusterOperations.Where(o => o.Id == _operationId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Log, o => o.Log + text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush operation log for {Op}", _operationId);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                await FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _pump;
        await FlushAsync();
        _cts.Dispose();
    }
}
