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

        // 1. Incremental cross-repo linking for this repo
        await RunStepAsync(async services =>
        {
            var sw = Stopwatch.StartNew();
            logger.LogInformation("Running incremental cross-repo linking for {Repo}", message.Name);
            await services.GetRequiredService<CrossRepoLinker>().LinkForProjectAsync(message.Name, ct);
            logger.LogInformation("[Timing] Cross-repo linking for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
        }, ex => logger.LogWarning(ex, "Cross-repo linking failed for {Repo} — continuing", message.Name));

        // 1.5. Re-run community detection (Louvain clustering)
        if (indexingOptions.DetectCommunitiesAfterIndexing)
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
            await RunStepAsync(async services =>
            {
                var vitalsAnalyzer = services.GetRequiredService<IVitalsAnalyzer>();
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Computing vitals metrics for {Repo}", message.Name);
                await vitalsAnalyzer.ComputeMetricsAsync(message.Name, message.RepoPath, ct);
                logger.LogInformation("[Timing] Vitals metrics for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                sw.Restart();
                logger.LogInformation("Analyzing health for {Repo}", message.Name);
                await vitalsAnalyzer.AnalyzeHealthAsync(message.Name, ct);
                logger.LogInformation("[Timing] Health analysis for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }, ex => logger.LogWarning(ex, "Vitals/health analysis failed for {Repo} — continuing", message.Name));

            // 2.5 Security scan
            await RunStepAsync(async services =>
            {
                var graphStore = services.GetRequiredService<IGraphStore>();
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Running security scan for {Repo}", message.Name);
                var secResult = await services.GetRequiredService<ISecurityAnalyzer>()
                    .ScanAsync(message.Name, message.RepoPath, ct);
                logger.LogInformation("[Timing] Security scan for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                var summaries = await graphStore.GetProjectHealthSummariesAsync(message.Name);
                foreach (var summary in summaries)
                {
                    var blended = Math.Round(0.80 * summary.OverallHealth + 0.20 * secResult.SecurityScore, 1);
                    summary.OverallHealth = blended;
                    await graphStore.UpsertProjectHealthSummaryAsync(summary);
                }
            }, ex => logger.LogWarning(ex, "Security scan failed for {Repo} — continuing", message.Name));
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
