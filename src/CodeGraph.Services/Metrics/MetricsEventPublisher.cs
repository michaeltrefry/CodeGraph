using CodeGraph.Models.Messages;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Metrics;

public class MetricsEventPublisher(
    IMessageBus messageBus,
    ILogger<MetricsEventPublisher> logger) : IMetricsEventPublisher
{
    public async Task<LlmUsageRecord> PublishLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = MetricsEventNormalizer.Normalize(usage);
        logger.LogInformation(
            "Publishing LLM usage for {Path} via {Provider}/{Model} user {Username} tokens {TotalTokens}",
            normalized.Path,
            normalized.Provider,
            normalized.Model,
            normalized.Username,
            normalized.TotalTokens);

        await messageBus.PublishAsync(ToMessage(normalized), ct);
        return normalized;
    }

    public async Task<IReadOnlyList<LlmUsageRecord>> PublishLlmUsageBatchAsync(
        IEnumerable<LlmUsageRecord> usage,
        CancellationToken ct = default)
    {
        var normalized = usage.Select(MetricsEventNormalizer.Normalize).ToList();
        foreach (var record in normalized)
            ct.ThrowIfCancellationRequested();

        foreach (var record in normalized)
            await messageBus.PublishAsync(ToMessage(record), ct);

        return normalized;
    }

    public async Task<McpToolInvocationRecord> PublishMcpToolInvocationAsync(
        McpToolInvocationRecord invocation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = MetricsEventNormalizer.Normalize(invocation);
        logger.LogInformation(
            "Publishing MCP tool invocation for {ToolName} success {Success} user {Username} durationMs {DurationMs}",
            normalized.ToolName,
            normalized.Success,
            normalized.Username ?? "anonymous",
            normalized.DurationMs);

        await messageBus.PublishAsync(ToMessage(normalized), ct);
        return normalized;
    }

    private static LlmUsageRecorded ToMessage(LlmUsageRecord usage) => new()
    {
        EventId = usage.EventId ?? Guid.NewGuid().ToString("N"),
        Username = usage.Username,
        Path = usage.Path,
        Provider = usage.Provider,
        Model = usage.Model,
        InputTokens = usage.InputTokens,
        OutputTokens = usage.OutputTokens,
        TotalTokens = usage.TotalTokens,
        CreatedAt = usage.CreatedAt ?? DateTime.UtcNow
    };

    private static McpToolInvocationRecorded ToMessage(McpToolInvocationRecord invocation) => new()
    {
        EventId = invocation.EventId ?? Guid.NewGuid().ToString("N"),
        Username = invocation.Username,
        TokenId = invocation.TokenId,
        ToolName = invocation.ToolName,
        Success = invocation.Success,
        DurationMs = checked((int)Math.Min(int.MaxValue, invocation.DurationMs)),
        ErrorCode = invocation.ErrorCode,
        CreatedAt = invocation.CreatedAt ?? DateTime.UtcNow
    };
}
