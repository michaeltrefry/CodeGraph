namespace CodeGraph.Services.Usage;

public sealed record LlmUsageRecord(
    string Username,
    string Path,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    DateTime? CreatedAt = null,
    string? EventId = null);
