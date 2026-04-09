using CodeGraph.Data;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Reviews;

public class RepositoryReviewRecoveryService(
    IGraphStore store,
    ILogger<RepositoryReviewRecoveryService> logger) : IRepositoryReviewRecoveryService
{
    public async Task RecoverInterruptedRunsAsync(CancellationToken ct = default)
    {
        var orphanedRuns = await store.GetRepositoryReviewRunsByStatusAsync(["queued", "running"]);
        if (orphanedRuns.Count == 0)
            return;

        var completedAt = DateTime.UtcNow;
        foreach (var run in orphanedRuns)
        {
            ct.ThrowIfCancellationRequested();

            var message = run.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
                ? "Repository review was interrupted while the API was restarting. Continue Review to restart it."
                : "Repository review was queued when the API restarted before work began. Continue Review to restart it.";

            await store.UpdateRepositoryReviewRunStatusAsync(
                run.Id,
                "interrupted",
                completedAt: completedAt,
                error: message);

            logger.LogWarning(
                "Marked repository review {ReviewRunId} for {Repo} as interrupted during startup recovery (previous status: {Status})",
                run.Id,
                run.Repo,
                run.Status);
        }
    }
}
