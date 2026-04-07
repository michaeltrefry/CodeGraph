using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Api.Consumers;

/// <summary>
/// Handles post-indexing work: cross-repo linking, vitals computation, and analysis submission.
/// Each responsibility is independent — failures in one don't block the others.
/// </summary>
public class RepositoryIndexingCompletedConsumer(
    CrossRepoLinker linker,
    ICommunityDetectionService communityDetection,
    IVitalsAnalyzer vitalsAnalyzer,
    ISecurityAnalyzer securityAnalyzer,
    IBatchAnalysisService batchService,
    IGraphStore graphStore,
    IOptions<IndexingOptions> indexingOptionsAccessor,
    ILogger<RepositoryIndexingCompletedConsumer> logger) : IConsumer<RepositoryIndexingCompleted>
{
    private readonly IndexingOptions indexingOptions = indexingOptionsAccessor.Value;
    public async Task Consume(ConsumeContext<RepositoryIndexingCompleted> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        // 1. Incremental cross-repo linking for this repo
        try
        {
            var sw = Stopwatch.StartNew();
            logger.LogInformation("Running incremental cross-repo linking for {Repo}", message.Name);
            await linker.LinkForProjectAsync(message.Name, ct);
            logger.LogInformation("[Timing] Cross-repo linking for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cross-repo linking failed for {Repo} — continuing", message.Name);
        }

        // 1.5. Re-run community detection (Louvain clustering)
        if (indexingOptions.DetectCommunitiesAfterIndexing)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await communityDetection.DetectCommunitiesAsync(ct);
                logger.LogInformation("[Timing] Community detection for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{Repo}] Community detection failed — continuing", message.Name);
            }
        }

        // 2. Compute vitals metrics and health analysis
        if (message.ShouldComputeVitals)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Computing vitals metrics for {Repo}", message.Name);
                await vitalsAnalyzer.ComputeMetricsAsync(message.Name, message.RepoPath, ct);
                logger.LogInformation("[Timing] Vitals metrics for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                sw.Restart();
                logger.LogInformation("Analyzing health for {Repo}", message.Name);
                await vitalsAnalyzer.AnalyzeHealthAsync(message.Name, ct);
                logger.LogInformation("[Timing] Health analysis for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Vitals/health analysis failed for {Repo} — continuing", message.Name);
            }

            // 2.5 Security scan
            try
            {
                var sw = Stopwatch.StartNew();
                logger.LogInformation("Running security scan for {Repo}", message.Name);
                var secResult = await securityAnalyzer.ScanAsync(message.Name, message.RepoPath, ct);
                logger.LogInformation("[Timing] Security scan for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                var summaries = await graphStore.GetProjectHealthSummariesAsync(message.Name);
                foreach (var summary in summaries)
                {
                    var blended = Math.Round(0.80 * summary.OverallHealth + 0.20 * secResult.SecurityScore, 1);
                    summary.OverallHealth = blended;
                    await graphStore.UpsertProjectHealthSummaryAsync(summary);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Security scan failed for {Repo} — continuing", message.Name);
            }
        }

        // 3. Submit analysis batch
        if (message.ShouldAnalyze)
        {
            try
            {
                var allNodes = await graphStore.GetAllNodesByProjectAsync(message.Name);
                var hasAnalyzableNodes = allNodes.Any(n =>
                    n.Label is "Class" or "Interface");

                if (hasAnalyzableNodes)
                {
                    logger.LogInformation("Submitting analysis batch for {Repo}", message.Name);
                    await batchService.SubmitAnalysisBatchAsync(message.Name, message.RepoPath, message.IncludeAllSource, ct);
                }
                else
                {
                    logger.LogWarning("No analyzable nodes found for {Repo} — skipping analysis", message.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis submission failed for {Repo} — continuing", message.Name);
            }
        }
    }
}
