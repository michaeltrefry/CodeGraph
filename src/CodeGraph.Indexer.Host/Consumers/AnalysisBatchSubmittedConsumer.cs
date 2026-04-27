using MassTransit;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Indexer.Host.Consumers;

/// <summary>
/// Checks a queued analysis batch for completion. If the batch isn't done yet,
/// throws BatchNotReadyException to trigger delayed redelivery via MassTransit.
/// </summary>
public class AnalysisBatchSubmittedConsumer(
    IBatchAnalysisService batchService,
    IGraphStore store,
    ILogger<AnalysisBatchSubmittedConsumer> logger) : IConsumer<AnalysisBatchSubmitted>
{
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromMinutes(1);

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
                "Batch {BatchId} for {Repo} still processing — scheduling another check in {DelaySeconds}s",
                message.ProviderBatchId, message.RepoName, (int)RecheckDelay.TotalSeconds);

            await context.SchedulePublish(DateTime.UtcNow + RecheckDelay, new AnalysisBatchSubmitted
            {
                RepoName = message.RepoName,
                ProviderBatchId = message.ProviderBatchId,
                RequestCount = message.RequestCount
            });
            return;
        }

        logger.LogInformation("Batch {BatchId} for {Repo} completed and processed",
            message.ProviderBatchId, message.RepoName);
    }
}
