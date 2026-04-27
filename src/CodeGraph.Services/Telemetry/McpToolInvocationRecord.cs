namespace CodeGraph.Services.Telemetry;

public sealed record McpToolInvocationRecord(
    string ToolName,
    bool Success,
    long DurationMs,
    string? Username = null,
    long? TokenId = null,
    string? ErrorCode = null,
    DateTime? CreatedAt = null,
    string? EventId = null);
