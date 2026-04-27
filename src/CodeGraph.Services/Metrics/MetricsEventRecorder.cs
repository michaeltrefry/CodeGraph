using CodeGraph.Data;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Metrics;

public class MetricsEventRecorder(
    IMetricsEventStore store,
    ILogger<MetricsEventRecorder> logger) : IMetricsEventRecorder
{
    public async Task<LlmUsageRecord> RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = MetricsEventNormalizer.Normalize(usage);
        logger.LogInformation(
            "Recording LLM usage for {Path} via {Provider}/{Model} user {Username} tokens {TotalTokens}",
            normalized.Path,
            normalized.Provider,
            normalized.Model,
            normalized.Username,
            normalized.TotalTokens);

        await store.CreateLlmUsageAsync(ToEntity(normalized));
        return normalized;
    }

    public async Task<IReadOnlyList<LlmUsageRecord>> RecordLlmUsageBatchAsync(
        IEnumerable<LlmUsageRecord> usage,
        CancellationToken ct = default)
    {
        var normalized = usage.Select(MetricsEventNormalizer.Normalize).ToList();
        foreach (var record in normalized)
            ct.ThrowIfCancellationRequested();

        await store.CreateLlmUsageBatchAsync(normalized.Select(ToEntity));
        return normalized;
    }

    public async Task<McpToolInvocationRecord> RecordMcpToolInvocationAsync(
        McpToolInvocationRecord invocation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = MetricsEventNormalizer.Normalize(invocation);
        logger.LogInformation(
            "Recording MCP tool invocation for {ToolName} success {Success} user {Username} durationMs {DurationMs}",
            normalized.ToolName,
            normalized.Success,
            normalized.Username ?? "anonymous",
            normalized.DurationMs);

        await store.CreateMcpToolInvocationAsync(ToEntity(normalized));
        return normalized;
    }

    private static LlmUsageEntity ToEntity(LlmUsageRecord usage) => new()
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

    private static McpToolInvocationEntity ToEntity(McpToolInvocationRecord invocation) => new()
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
