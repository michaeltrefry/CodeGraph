using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Host.Shared.Consumers;

/// <summary>
/// Handles post-indexing work: cross-repo linking, vitals computation, and analysis submission.
/// Each responsibility is independent — failures in one don't block the others.
/// </summary>
/// <remarks>
/// Every step resolves its services from its own DI scope, so each gets its own DbContext. A
/// step that fails mid-operation (an OutOfMemoryException or timeout while a query was in flight)
/// can leave its context unusable; sharing the message scope's context then failed every later
/// step with "A second operation was started on this context instance" (sc-25698).
/// </remarks>
public class RepositoryIndexingCompletedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<IndexingOptions> indexingOptionsAccessor,
    ILogger<RepositoryIndexingCompletedConsumer> logger) : IConsumer<RepositoryIndexingCompleted>
{
    private readonly IndexingOptions indexingOptions = indexingOptionsAccessor.Value;

    public async Task Consume(ConsumeContext<RepositoryIndexingCompleted> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        // 1. Incremental cross-repo linking for this repo. A vitals-only refresh did not change the
        // graph, so linking and community detection are skipped.
        if (!message.VitalsRefreshOnly)
        {
            await RunStepAsync(async services =>
            {
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Running incremental cross-repo linking for {Repo}", message.Name);
                await services.GetRequiredService<CrossRepoLinker>().LinkForProjectAsync(message.Name, ct);
                logger.LogInformation("[Timing] Cross-repo linking for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }, ex => logger.LogWarning(ex, "Cross-repo linking failed for {Repo} — continuing", message.Name));
        }

        // 1.5. Re-run community detection (Louvain clustering)
        if (indexingOptions.DetectCommunitiesAfterIndexing && !message.VitalsRefreshOnly)
        {
            await RunStepAsync(async services =>
            {
                var sw = Stopwatch.StartNew();
                await services.GetRequiredService<ICommunityDetectionService>().DetectCommunitiesAsync(ct);
                logger.LogInformation("[Timing] Community detection for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }, ex => logger.LogWarning(ex, "[{Repo}] Community detection failed — continuing", message.Name));
        }

        // 2. Compute vitals metrics and health analysis
        if (message.ShouldComputeVitals)
        {
            // Health summaries written by this run's vitals computation are the only ones the
            // security score may be blended into: blending a summary again (a failed or skipped
            // vitals run leaves the previous, already blended rows) would compound the blend.
            var vitalsStartedAt = DateTime.UtcNow.AddSeconds(-1);
            var metricsComputed = false;
            var versionRecorded = false;
            Exception? vitalsFailure = null;
            await RunStepAsync(async services =>
            {
                var vitalsAnalyzer = services.GetRequiredService<IVitalsAnalyzer>();
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Computing vitals metrics for {Repo}", message.Name);
                await vitalsAnalyzer.ComputeMetricsAsync(message.Name, message.RepoPath, ct);
                metricsComputed = true;
                logger.LogInformation("[Timing] Vitals metrics for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
                // Routine syncs recompute vitals at an unchanged HEAD while this differs from the
                // running analyzer's version.
                await services.GetRequiredService<IGraphStore>()
                    .SetVitalsVersionAsync(message.Name, VitalsAnalyzer.Version);
                versionRecorded = true;

                // Health analysis calls the analysis model: only when analysis or health analysis
                // was requested (an explicit re-analyze submits its batch itself, so it asks for
                // health analysis alone).
                if (!message.ShouldAnalyze && !message.ShouldAnalyzeHealth)
                {
                    logger.LogInformation("Skipping health analysis for {Repo}: analysis not requested", message.Name);
                    return;
                }

                sw.Restart();
                logger.LogInformation("Analyzing health for {Repo}", message.Name);
                await vitalsAnalyzer.AnalyzeHealthAsync(message.Name, ct);
                logger.LogInformation("[Timing] Health analysis for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }, ex =>
            {
                vitalsFailure = ex;
                logger.LogWarning(ex, "Vitals/health analysis failed for {Repo} — continuing", message.Name);
            });

            if (!versionRecorded && vitalsFailure is not null)
                await RecordVitalsFailureAsync(message, vitalsFailure);

            // 2.5 Security scan. A vitals-only refresh whose vitals failed has nothing to re-blend.
            if (metricsComputed || !message.VitalsRefreshOnly)
            {
                await RunStepAsync(async services =>
                {
                    var graphStore = services.GetRequiredService<IGraphStore>();
                    var sw = Stopwatch.StartNew();
                    logger.LogInformation("Running security scan for {Repo}", message.Name);
                    var secResult = await services.GetRequiredService<ISecurityAnalyzer>()
                        .ScanAsync(message.Name, message.RepoPath, ct);
                    logger.LogInformation("[Timing] Security scan for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                    if (!metricsComputed)
                    {
                        logger.LogWarning("Security score not blended into health for {Repo}: vitals were not computed", message.Name);
                        return;
                    }

                    var summaries = await graphStore.GetProjectHealthSummariesAsync(message.Name);
                    foreach (var summary in summaries.Where(summary => summary.ComputedAt >= vitalsStartedAt))
                    {
                        var blended = Math.Round(0.80 * summary.OverallHealth + 0.20 * secResult.SecurityScore, 1);
                        summary.OverallHealth = blended;
                        await graphStore.UpsertProjectHealthSummaryAsync(summary);
                    }
                }, ex => logger.LogWarning(ex, "Security scan failed for {Repo} — continuing", message.Name));
            }
        }

        // 3. Submit analysis batch
        if (message.ShouldAnalyze)
        {
            await RunStepAsync(async services =>
            {
                // Count by label instead of loading every node row of the repository.
                var counts = await services.GetRequiredService<IGraphStore>()
                    .GetNodeCountsByLabelForProjectAsync(message.Name);
                var hasAnalyzableNodes = counts.GetValueOrDefault("Class") > 0 ||
                                         counts.GetValueOrDefault("Interface") > 0;

                if (hasAnalyzableNodes)
                {
                    logger.LogInformation("Submitting analysis batch for {Repo}", message.Name);
                    await services.GetRequiredService<IBatchAnalysisService>()
                        .SubmitAnalysisBatchAsync(message.Name, message.RepoPath, message.IncludeAllSource, ct);
                }
                else
                {
                    logger.LogWarning("No analyzable nodes found for {Repo} — skipping analysis", message.Name);
                }
            }, ex => logger.LogWarning(ex, "Analysis submission failed for {Repo} — continuing", message.Name));
        }
    }

    /// <summary>
    /// Counts the failed vitals computation so routine syncs stop retrying a vitals-only refresh of
    /// the same commit and vitals version at the limit; logs the cause at Error once, on reaching it.
    /// </summary>
    private async Task RecordVitalsFailureAsync(RepositoryIndexingCompleted message, Exception failure)
    {
        await RunStepAsync(async services =>
        {
            var failures = await services.GetRequiredService<IGraphStore>()
                .RecordVitalsFailureAsync(message.Name, message.CommitSha, VitalsAnalyzer.Version);
            var limit = indexingOptions.MaxConsecutiveIndexFailures;
            if (limit > 0 && failures == limit)
            {
                logger.LogError(failure,
                    "Vitals for {Repo} at {Sha} (vitals version {Version}) have failed {Failures} consecutive times; routine syncs stop recomputing them until the commit or the vitals version changes",
                    message.Name, message.CommitSha ?? "unknown", VitalsAnalyzer.Version, failures);
            }
        }, ex => logger.LogError(ex, "Could not record the vitals failure of {Repo}", message.Name));
    }

    private async Task RunStepAsync(Func<IServiceProvider, Task> step, Action<Exception> onFailure)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await step(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            onFailure(ex);
        }
    }
}
