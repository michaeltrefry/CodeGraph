using MassTransit;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Api.Consumers;

/// <summary>
/// Checks an Anthropic batch for completion. If the batch isn't done yet,
/// throws BatchNotReadyException to trigger delayed redelivery via MassTransit.
/// </summary>
public class AnalysisBatchSubmittedConsumer(
    IBatchAnalysisService batchService,
    IGraphStore store,
    ILogger<AnalysisBatchSubmittedConsumer> logger) : IConsumer<AnalysisBatchSubmitted>
{
    public async Task Consume(ConsumeContext<AnalysisBatchSubmitted> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await batchService.ProcessCompletedBatchesAsync(message.RepoName, ct);

        var pending = await store.GetPendingBatchesAsync(message.RepoName);
        var stillPending = pending.Any(b => b.AnthropicBatchId == message.AnthropicBatchId);

        if (stillPending)
        {
            logger.LogInformation(
                "Batch {BatchId} for {Repo} still processing — will retry via redelivery",
                message.AnthropicBatchId, message.RepoName);

            throw new BatchNotReadyException(
                $"Batch {message.AnthropicBatchId} for {message.RepoName} is still processing");
        }

        logger.LogInformation("Batch {BatchId} for {Repo} completed and processed",
            message.AnthropicBatchId, message.RepoName);
    }
}

public class BatchNotReadyException : Exception
{
    public BatchNotReadyException(string message) : base(message) { }
    public BatchNotReadyException(string message, Exception innerException) : base(message, innerException) { }
}
