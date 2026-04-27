namespace CodeGraph.Models.Responses;

public record StartAssistantRunResponse(
    long RunId,
    string ChatId,
    string Status,
    string StreamUrl,
    string EventsUrl,
    string GetUrl);

public record AssistantRunResponse(
    long Id,
    string ChatId,
    string Username,
    string Status,
    string Question,
    string? Context,
    string? ProviderRequested,
    string? ModelRequested,
    string? ProviderUsed,
    string? ModelUsed,
    string? FinalAnswer,
    IReadOnlyList<string> Warnings,
    string? Error,
    long LastSequence,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public record AssistantRunEventResponse(
    long Sequence,
    string Type,
    object? Content,
    DateTime CreatedAt);

public record AssistantRunEventsResponse(
    AssistantRunResponse Run,
    IReadOnlyList<AssistantRunEventResponse> Events);

public record AssistantDebugExchangeResponse(
    long ExchangeIndex,
    int TurnIndex,
    string Provider,
    string Model,
    object RequestBody,
    object? ResponseBody,
    string RequestText,
    string? ResponseText,
    object? ToolUses,
    object? RequestMetadata,
    object? ResponseMetadata,
    string? RequestId,
    string? ResponseId,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    DateTime CreatedAt);

public record AssistantDebugExchangeListResponse(
    AssistantRunResponse Run,
    IReadOnlyList<AssistantDebugExchangeResponse> Exchanges);

public record AssistantRunConflictResponse(
    string Error,
    string Message,
    long? MismatchMessageIndex = null,
    long? ExistingRunId = null);

public record AssistantChatMessageResponse(
    long MessageIndex,
    string Role,
    string Content,
    long? SourceRunId,
    DateTime CreatedAt);

public record AssistantChatSummaryResponse(
    string ChatId,
    string Title,
    string Status,
    long? ActiveRunId,
    DateTime LastActivityAt);

public record AssistantChatTranscriptResponse(
    string ChatId,
    string Title,
    IReadOnlyList<AssistantChatMessageResponse> Messages,
    AssistantRunResponse? ActiveRun,
    DateTime LastActivityAt);
