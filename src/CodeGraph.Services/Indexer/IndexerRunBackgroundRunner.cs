using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Indexer;

public sealed class IndexerRunWorkerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 3;
}

public sealed class IndexerRunBackgroundRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<IndexerRunWorkerOptions> optionsAccessor,
    ILogger<IndexerRunBackgroundRunner> logger) : BackgroundService, IIndexerRunBackgroundRunner
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public Task EnqueueAsync(long runId, CancellationToken ct = default)
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; persisted work will be drained in order.
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TryExecuteNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Durable indexer worker failed while polling for work");
            }

            try
            {
                await _wake.WaitAsync(optionsAccessor.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<bool> TryExecuteNextAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        ValidateOptions(options);

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIndexerRunStore>();
        var now = DateTime.UtcNow;
        var lease = await store.TryClaimNextIndexerRunAsync(
            _owner,
            now,
            now + options.LeaseDuration,
            options.MaxAttempts,
            stoppingToken);
        if (lease is null)
            return false;

        var executor = scope.ServiceProvider.GetRequiredService<IndexerRunExecutor>();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var execution = executor.ExecuteAsync(lease, executionCancellation.Token);
        var monitor = MonitorLeaseAsync(lease, options, monitorCancellation.Token);

        try
        {
            var winner = await Task.WhenAny(execution, monitor);
            if (winner == monitor)
            {
                var leaseState = await monitor;
                executionCancellation.Cancel();
                try
                {
                    await execution;
                }
                catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
                {
                    // Expected when cancellation or lease loss reaches cooperative work.
                }

                if (leaseState == LeaseMonitorResult.CancellationRequested)
                {
                    await store.CancelOwnedIndexerRunAsync(
                        lease,
                        "Canceled by request while the operation was running.",
                        DateTime.UtcNow,
                        CancellationToken.None);
                }
                else
                {
                    logger.LogWarning(
                        "Indexer run {RunId} lost lease ownership; fencing token {FencingToken} prevents stale completion",
                        lease.Run.Id,
                        lease.FencingToken);
                }

                return true;
            }

            var message = await execution;
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitor);
            if (!await store.CompleteIndexerRunAsync(lease, message, DateTime.UtcNow, CancellationToken.None))
            {
                logger.LogWarning(
                    "Indexer run {RunId} completed work after losing lease; terminal update was fenced out",
                    lease.Run.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Leave the durable lease in place. A retry-safe run will be recovered after
            // expiry; an ambiguous non-idempotent run will be failed without replay.
            throw;
        }
        catch (Exception ex)
        {
            executionCancellation.Cancel();
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitor);
            var disposition = await store.FailOrRetryIndexerRunAsync(
                lease,
                ex.Message,
                DateTime.UtcNow,
                DateTime.UtcNow + options.RetryDelay,
                options.MaxAttempts,
                CancellationToken.None);
            logger.LogWarning(
                ex,
                "Indexer run {RunId} failed with durable disposition {Disposition}",
                lease.Run.Id,
                disposition);
        }

        return true;
    }

    private async Task<LeaseMonitorResult> MonitorLeaseAsync(
        IndexerRunLease lease,
        IndexerRunWorkerOptions options,
        CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(options.HeartbeatInterval, ct);
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IIndexerRunStore>();
            var now = DateTime.UtcNow;
            var renewal = await store.RenewIndexerRunLeaseAsync(
                lease.Run.Id,
                lease.Owner,
                lease.FencingToken,
                now,
                now + options.LeaseDuration,
                ct);
            if (!renewal.Renewed)
                return LeaseMonitorResult.LeaseLost;
            if (renewal.CancellationRequested)
                return LeaseMonitorResult.CancellationRequested;
        }
    }

    private static async Task IgnoreMonitorCancellationAsync(Task monitor)
    {
        try
        {
            await monitor;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ValidateOptions(IndexerRunWorkerOptions options)
    {
        if (options.HeartbeatInterval <= TimeSpan.Zero || options.LeaseDuration <= options.HeartbeatInterval)
            throw new InvalidOperationException("Indexer run lease duration must be greater than its positive heartbeat interval.");
        if (options.PollInterval <= TimeSpan.Zero || options.RetryDelay < TimeSpan.Zero || options.MaxAttempts < 1)
            throw new InvalidOperationException("Indexer run polling and retry settings are invalid.");
    }

    private enum LeaseMonitorResult
    {
        LeaseLost,
        CancellationRequested
    }
}
