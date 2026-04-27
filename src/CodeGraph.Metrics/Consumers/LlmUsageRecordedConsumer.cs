using CodeGraph.Models.Messages;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Usage;
using MassTransit;

namespace CodeGraph.Metrics.Consumers;

public class LlmUsageRecordedConsumer(
    IMetricsEventRecorder recorder,
    ILogger<LlmUsageRecordedConsumer> logger) : IConsumer<LlmUsageRecorded>
{
    public Task Consume(ConsumeContext<LlmUsageRecorded> context)
        => ProcessAsync(context.Message, context.CancellationToken);

    public async Task ProcessAsync(LlmUsageRecorded message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Consuming LLM usage for {Path} via {Provider}/{Model} user {Username} tokens {TotalTokens}",
            message.Path,
            message.Provider,
            message.Model,
            message.Username,
            message.TotalTokens);

        await recorder.RecordLlmUsageAsync(new LlmUsageRecord(
                message.Username,
                message.Path,
                message.Provider,
                message.Model,
                message.InputTokens,
                message.OutputTokens,
                message.TotalTokens,
                message.CreatedAt,
                message.EventId),
            cancellationToken);
    }
}
