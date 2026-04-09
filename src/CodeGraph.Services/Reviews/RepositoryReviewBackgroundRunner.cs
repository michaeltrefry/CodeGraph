using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Reviews;

public class RepositoryReviewBackgroundRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<RepositoryReviewBackgroundRunner> logger) : IRepositoryReviewBackgroundRunner
{
    private readonly object runnerLock = new();
    private readonly Dictionary<long, Task> runners = new();

    public Task EnqueueAsync(long reviewRunId, CancellationToken ct = default)
    {
        lock (runnerLock)
        {
            if (runners.TryGetValue(reviewRunId, out var existing) && !existing.IsCompleted)
                return Task.CompletedTask;

            var runner = Task.Run(() => RunInBackgroundAsync(reviewRunId), CancellationToken.None);
            runners[reviewRunId] = runner;

            _ = runner.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    logger.LogError(t.Exception,
                        "Repository review background runner crashed for review run {ReviewRunId}",
                        reviewRunId);
                }

                lock (runnerLock)
                {
                    if (runners.TryGetValue(reviewRunId, out var current) && ReferenceEquals(current, runner))
                        runners.Remove(reviewRunId);
                }
            }, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    private async Task RunInBackgroundAsync(long reviewRunId)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IRepositoryReviewService>();
        await service.ExecuteReviewRunAsync(reviewRunId, CancellationToken.None);
    }
}
