using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Services.Analyzers;
using TC.CodeGraphApi.Services.Pipeline;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

/// <summary>
/// Handles post-indexing work: cross-repo linking, vitals computation, and analysis submission.
/// Each responsibility is independent — failures in one don't block the others.
/// </summary>
public class RepositoryIndexingCompletedConsumer : TcConsumer<RepositoryIndexingCompleted, RepositoryIndexingCompletedConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;
    private readonly ILogger<RepositoryIndexingCompletedConsumer> _logger1;

    /// <summary>
    /// Handles post-indexing work: cross-repo linking, vitals computation, and analysis submission.
    /// Each responsibility is independent — failures in one don't block the others.
    /// </summary>
    public RepositoryIndexingCompletedConsumer(IScope scope, 
        ITcConfiguration<CodeGraphServiceSettings> settings,
        ILogger<RepositoryIndexingCompletedConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        _logger1 = logger;
        TcConsumerConfigurator.QueueSuffix = typeof(RepositoryIndexingCompleted).FullName;
    }

    public override Action<IInstanceConfigurator<RepositoryIndexingCompletedConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }
    
    public override async Task Consume(RepositoryIndexingCompleted message, ConsumeContext<RepositoryIndexingCompleted> consumeContext)
    {
        var ct = consumeContext.CancellationToken;

        // 1. Incremental cross-repo linking for this repo
        try
        {
            var sw = Stopwatch.StartNew();
            using var linkScope = _scope.CreateChildScope();
            var linker = linkScope.GetInstance<CrossRepoLinker>();
            _logger1.LogInformation("Running incremental cross-repo linking for {Repo}", message.Name);
            await linker.LinkForProjectAsync(message.Name, ct);
            _logger1.LogInformation("[Timing] Cross-repo linking for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger1.LogWarning(ex, "Cross-repo linking failed for {Repo} — continuing", message.Name);
        }

        // 1.5. Re-run community detection (Louvain clustering) — milliseconds, always fresh
        try
        {
            var sw = Stopwatch.StartNew();
            using var clusterScope = _scope.CreateChildScope();
            var detector = clusterScope.GetInstance<ICommunityDetectionService>();
            await detector.DetectCommunitiesAsync(ct);
            _logger1.LogInformation("[Timing] Community detection for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger1.LogWarning(ex, "[{Repo}] Community detection failed — continuing", message.Name);
        }

        // 2. Compute vitals metrics and health analysis
        if (message.ShouldComputeVitals)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var vitalsScope = _scope.CreateChildScope();
                var vitalsAnalyzer = vitalsScope.GetInstance<IVitalsAnalyzer>();

                _logger1.LogInformation("Computing vitals metrics for {Repo}", message.Name);
                await vitalsAnalyzer.ComputeMetricsAsync(message.Name, message.RepoPath, ct);
                _logger1.LogInformation("[Timing] Vitals metrics for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                sw.Restart();
                _logger1.LogInformation("Analyzing health for {Repo}", message.Name);
                await vitalsAnalyzer.AnalyzeHealthAsync(message.Name, ct);
                _logger1.LogInformation("[Timing] Health analysis for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger1.LogWarning(ex, "Vitals/health analysis failed for {Repo} — continuing", message.Name);
            }

            // 2.5 Security scan — runs after vitals, blends score into health summaries
            try
            {
                var sw = Stopwatch.StartNew();
                using var secScope = _scope.CreateChildScope();
                var secAnalyzer = secScope.GetInstance<ISecurityAnalyzer>();
                var graphStore = secScope.GetInstance<IGraphStore>();

                _logger1.LogInformation("Running security scan for {Repo}", message.Name);
                var secResult = await secAnalyzer.ScanAsync(message.Name, message.RepoPath, ct);
                _logger1.LogInformation("[Timing] Security scan for {Repo}: {ElapsedMs}ms", message.Name, sw.ElapsedMilliseconds);

                // Blend security score into existing health summaries (80% vitals + 20% security)
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
                _logger1.LogWarning(ex, "Security scan failed for {Repo} — continuing", message.Name);
            }
        }

        // 3. Submit analysis batch
        if (message.ShouldAnalyze)
        {
            try
            {
                using var analysisScope = _scope.CreateChildScope();
                var graphStore = analysisScope.GetInstance<IGraphStore>();
                var batchService = analysisScope.GetInstance<IBatchAnalysisService>();

                var allNodes = await graphStore.GetAllNodesByProjectAsync(message.Name);
                var hasAnalyzableNodes = allNodes.Any(n =>
                    n.Label is "Class" or "Interface"
                        or "Playbook" or "Role" or "AnsibleTask" or "AnsibleHandler" or "AnsibleVariable"
                        or "TerraformResource" or "TerraformModule" or "TerraformVariable" or "TerraformOutput" or "TerraformDataSource");

                if (hasAnalyzableNodes)
                {
                    _logger1.LogInformation("Submitting analysis batch for {Repo}", message.Name);
                    await batchService.SubmitAnalysisBatchAsync(message.Name, message.RepoPath, message.IncludeAllSource, ct);
                }
                else
                {
                    _logger1.LogWarning("No analyzable nodes found for {Repo} — skipping analysis", message.Name);
                }
            }
            catch (Exception ex)
            {
                _logger1.LogWarning(ex, "Analysis submission failed for {Repo} — continuing", message.Name);
            }
        }
    }
}
