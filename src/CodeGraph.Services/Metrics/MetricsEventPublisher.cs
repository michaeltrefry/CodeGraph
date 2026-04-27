using CodeGraph.Data;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Metrics;

public class MetricsEventPublisher(
    IMetricsEventStore store,
    ILogger<MetricsEventPublisher> logger) : IMetricsEventPublisher
{
    public async Task<LlmUsageRecord> PublishLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = Normalize(usage);
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

    public async Task<IReadOnlyList<LlmUsageRecord>> PublishLlmUsageBatchAsync(
        IEnumerable<LlmUsageRecord> usage,
        CancellationToken ct = default)
    {
        var normalized = usage.Select(Normalize).ToList();
        foreach (var record in normalized)
            ct.ThrowIfCancellationRequested();

        await store.CreateLlmUsageBatchAsync(normalized.Select(ToEntity));
        return normalized;
    }

    public async Task<McpToolInvocationRecord> PublishMcpToolInvocationAsync(
        McpToolInvocationRecord invocation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = Normalize(invocation);
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
        EventId = string.IsNullOrWhiteSpace(usage.EventId) ? Guid.NewGuid().ToString("N") : usage.EventId.Trim(),
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
        EventId = string.IsNullOrWhiteSpace(invocation.EventId) ? Guid.NewGuid().ToString("N") : invocation.EventId.Trim(),
        Username = invocation.Username,
        TokenId = invocation.TokenId,
        ToolName = invocation.ToolName,
        Success = invocation.Success,
        DurationMs = checked((int)Math.Min(int.MaxValue, invocation.DurationMs)),
        ErrorCode = invocation.ErrorCode,
        CreatedAt = invocation.CreatedAt ?? DateTime.UtcNow
    };

    private static LlmUsageRecord Normalize(LlmUsageRecord usage)
    {
        var inputTokens = Math.Max(0, usage.InputTokens);
        var outputTokens = Math.Max(0, usage.OutputTokens);
        var totalTokens = usage.TotalTokens > 0 ? usage.TotalTokens : inputTokens + outputTokens;

        return usage with
        {
            Username = string.IsNullOrWhiteSpace(usage.Username) ? "system" : usage.Username.Trim().ToLowerInvariant(),
            Path = string.IsNullOrWhiteSpace(usage.Path) ? "Unknown" : usage.Path.Trim(),
            Provider = string.IsNullOrWhiteSpace(usage.Provider) ? "unknown" : usage.Provider.Trim(),
            Model = string.IsNullOrWhiteSpace(usage.Model) ? "unknown" : usage.Model.Trim(),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = Math.Max(0, totalTokens),
            CreatedAt = usage.CreatedAt ?? DateTime.UtcNow
        };
    }

    private static McpToolInvocationRecord Normalize(McpToolInvocationRecord invocation)
    {
        return invocation with
        {
            Username = string.IsNullOrWhiteSpace(invocation.Username)
                ? null
                : invocation.Username.Trim().ToLowerInvariant(),
            ToolName = string.IsNullOrWhiteSpace(invocation.ToolName) ? "unknown_tool" : invocation.ToolName.Trim(),
            DurationMs = Math.Max(0, invocation.DurationMs),
            ErrorCode = NormalizeErrorCode(invocation.ErrorCode),
            CreatedAt = invocation.CreatedAt ?? DateTime.UtcNow
        };
    }

    private static string? NormalizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return null;

        var normalized = errorCode.Trim();
        return normalized.Length <= 255 ? normalized : normalized[..255];
    }
}
