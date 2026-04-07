using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Api.Consumers;

/// <summary>
/// Triggers repo-level synthesis after per-project analysis results are stored.
/// Publishes AnalysisSynthesisCompleted on success to trigger CODEGRAPH.md generation.
/// </summary>
public class ProjectAnalysisResultsProcessedConsumer(
    IBatchAnalysisService batchService,
    IMessageBus messageBus,
    ILogger<ProjectAnalysisResultsProcessedConsumer> logger) : IConsumer<ProjectAnalysisResultsProcessed>
{
    public async Task Consume(ConsumeContext<ProjectAnalysisResultsProcessed> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        try
        {
            logger.LogInformation("Synthesizing repo summary for {Repo} (batch {BatchId})",
                message.RepoName, message.ProviderBatchId);
            await batchService.SynthesizeRepoSummaryAsync(message.RepoName, message.ProviderBatchId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Repo synthesis failed for {Repo} — project results are still stored", message.RepoName);
        }

        // Always publish completion to trigger CODEGRAPH.md generation, even if synthesis failed
        await messageBus.PublishAsync(new AnalysisSynthesisCompleted
        {
            RepoName = message.RepoName,
            ProviderBatchId = message.ProviderBatchId
        });
    }
}
