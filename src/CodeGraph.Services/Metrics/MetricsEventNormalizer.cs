using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;

namespace CodeGraph.Services.Metrics;

internal static class MetricsEventNormalizer
{
    public static LlmUsageRecord Normalize(LlmUsageRecord usage)
    {
        var inputTokens = Math.Max(0, usage.InputTokens);
        var outputTokens = Math.Max(0, usage.OutputTokens);
        var totalTokens = usage.TotalTokens > 0 ? usage.TotalTokens : inputTokens + outputTokens;

        return usage with
        {
            EventId = string.IsNullOrWhiteSpace(usage.EventId) ? Guid.NewGuid().ToString("N") : usage.EventId.Trim(),
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

    public static McpToolInvocationRecord Normalize(McpToolInvocationRecord invocation)
    {
        return invocation with
        {
            EventId = string.IsNullOrWhiteSpace(invocation.EventId)
                ? Guid.NewGuid().ToString("N")
                : invocation.EventId.Trim(),
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
