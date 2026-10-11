using MassTransit;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Host.Shared.Consumers;

/// <summary>
/// Checks a newly submitted analysis batch once, right after submission (a provider that finishes
/// quickly is processed without waiting). A batch that is still running is left 'submitted' for
/// the periodic ProcessBatchAnalysis job, which sweeps every submitted batch.
/// </summary>
/// <remarks>
/// This consumer used to re-check every minute with <c>SchedulePublish</c>, which needs RabbitMQ's
/// delayed-message exchange plugin. The production broker does not have it, so the reschedule
/// faulted with 406 PRECONDITION_FAILED "unknown exchange type 'x-delayed-message'" (sc-25698).
/// The periodic sweep is the one mechanism that works on every broker, so the chain relies on it.
/// </remarks>
public class AnalysisBatchSubmittedConsumer(
    IBatchAnalysisService batchService,
    IGraphStore store,
    ILogger<AnalysisBatchSubmittedConsumer> logger) : IConsumer<AnalysisBatchSubmitted>
{
    public async Task Consume(ConsumeContext<AnalysisBatchSubmitted> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await batchService.ProcessCompletedBatchAsync(message.RepoName, message.ProviderBatchId, ct);

        var batch = await store.GetBatchByProviderBatchIdAsync(message.ProviderBatchId);
        var stillPending = batch is not null &&
            string.Equals(batch.Status, "submitted", StringComparison.OrdinalIgnoreCase);

        if (stillPending)
        {
            logger.LogInformation(
                "Batch {BatchId} for {Repo} still processing — the periodic batch-analysis job will process it when it completes",
                message.ProviderBatchId, message.RepoName);
            return;
        }

        logger.LogInformation("Batch {BatchId} for {Repo} completed and processed",
            message.ProviderBatchId, message.RepoName);
    }
}
