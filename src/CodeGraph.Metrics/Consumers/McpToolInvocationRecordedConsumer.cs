using CodeGraph.Models.Messages;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using MassTransit;

namespace CodeGraph.Metrics.Consumers;

public class McpToolInvocationRecordedConsumer(
    IMetricsEventRecorder recorder,
    ILogger<McpToolInvocationRecordedConsumer> logger) : IConsumer<McpToolInvocationRecorded>
{
    public Task Consume(ConsumeContext<McpToolInvocationRecorded> context)
        => ProcessAsync(context.Message, context.CancellationToken);

    public async Task ProcessAsync(McpToolInvocationRecorded message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Consuming MCP tool invocation for {ToolName} success {Success} user {Username} durationMs {DurationMs}",
            message.ToolName,
            message.Success,
            message.Username ?? "anonymous",
            message.DurationMs);

        await recorder.RecordMcpToolInvocationAsync(new McpToolInvocationRecord(
                message.ToolName,
                message.Success,
                message.DurationMs,
                message.Username,
                message.TokenId,
                message.ErrorCode,
                message.CreatedAt,
                message.EventId),
            cancellationToken);
    }
}
