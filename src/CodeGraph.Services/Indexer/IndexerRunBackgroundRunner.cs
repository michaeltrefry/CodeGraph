using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Indexer;

public sealed class IndexerRunBackgroundRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<IndexerRunBackgroundRunner> logger) : IIndexerRunBackgroundRunner
{
    private readonly object _lock = new();
    private readonly Dictionary<long, Task> _runners = new();

    public Task EnqueueAsync(long runId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_runners.TryGetValue(runId, out var existing) && !existing.IsCompleted)
                return Task.CompletedTask;

            var runner = Task.Run(() => RunInBackgroundAsync(runId), CancellationToken.None);
            _runners[runId] = runner;

            _ = runner.ContinueWith(task =>
            {
                if (task.IsFaulted)
                    logger.LogError(task.Exception, "Indexer background runner crashed for run {RunId}", runId);

                lock (_lock)
                {
                    if (_runners.TryGetValue(runId, out var current) && ReferenceEquals(current, runner))
                        _runners.Remove(runId);
                }
            }, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    private async Task RunInBackgroundAsync(long runId)
    {
        using var scope = scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IndexerRunExecutor>();
        await executor.ExecuteAsync(runId, CancellationToken.None);
    }
}
