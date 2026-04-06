using MassTransit;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services.Analyzers;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

/// <summary>
/// Triggers repo-level synthesis after per-project analysis results are stored.
/// Publishes AnalysisSynthesisCompleted on success to trigger CODEGRAPH.md generation.
/// </summary>
public class ProjectAnalysisResultsProcessedConsumer : TcConsumer<ProjectAnalysisResultsProcessed, ProjectAnalysisResultsProcessedConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;
    private readonly ILogger<ProjectAnalysisResultsProcessedConsumer> _logger1;

    /// <summary>
    /// Triggers repo-level synthesis after per-project analysis results are stored.
    /// Publishes AnalysisSynthesisCompleted on success to trigger CODEGRAPH.md generation.
    /// </summary>
    public ProjectAnalysisResultsProcessedConsumer(IScope scope, 
        ITcConfiguration<CodeGraphServiceSettings> settings, 
        ILogger<ProjectAnalysisResultsProcessedConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        _logger1 = logger;
        TcConsumerConfigurator.QueueSuffix = typeof(ProjectAnalysisResultsProcessed).FullName;
    }

    public override Action<IInstanceConfigurator<ProjectAnalysisResultsProcessedConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }
    
    public override async Task Consume(ProjectAnalysisResultsProcessed message, ConsumeContext<ProjectAnalysisResultsProcessed> consumeContext)
    {
        var ct = consumeContext.CancellationToken;

        try
        {
            using var childScope = _scope.CreateChildScope();
            var batchService = childScope.GetInstance<IBatchAnalysisService>();

            _logger1.LogInformation("Synthesizing repo summary for {Repo} (batch {BatchId})",
                message.RepoName, message.AnthropicBatchId);
            await batchService.SynthesizeRepoSummaryAsync(message.RepoName, message.AnthropicBatchId, ct);
        }
        catch (Exception ex)
        {
            _logger1.LogError(ex, "Repo synthesis failed for {Repo} — project results are still stored", message.RepoName);
        }

        // Always publish completion to trigger CODEGRAPH.md generation, even if synthesis failed
        // (per-project docs can still be generated from stored project analyses)
        var serviceBus = _scope.GetInstance<ITcServiceBus>();
        await serviceBus.Publish(new AnalysisSynthesisCompleted
        {
            RepoName = message.RepoName,
            AnthropicBatchId = message.AnthropicBatchId
        });
    }
}
