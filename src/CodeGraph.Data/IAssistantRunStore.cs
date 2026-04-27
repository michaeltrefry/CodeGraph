using CodeGraph.Models.Requests;

namespace CodeGraph.Data;

public record AssistantRunCreateRequest(
    string ChatId,
    string Username,
    string Question,
    string? Context,
    IReadOnlyList<ChatMessage> History,
    string? ProviderRequested,
    string? ModelRequested,
    string? IdempotencyKey,
    string RequestHash,
    DateTime CreatedAt);

public record AssistantRunConflict(
    string Code,
    string Message,
    long? MismatchMessageIndex = null,
    long? ExistingRunId = null);

public record AssistantRunCreateResult(
    AssistantRunEntity? Run,
    bool ReusedExisting = false,
    AssistantRunConflict? Conflict = null);

public record AssistantRunLeaseRenewalResult(
    bool Renewed,
    bool CancelRequested);

public record AssistantRunProgressUpdate(
    IReadOnlyList<AssistantRunEventEntity> Events,
    string? ExecutionStateJson = null);

public record AssistantRunTerminalUpdate(
    string Status,
    IReadOnlyList<AssistantRunEventEntity> Events,
    string? FinalAnswer = null,
    string? WarningsJson = null,
    DateTime? CompletedAt = null,
    string? Error = null,
    string? ProviderUsed = null,
    string? ModelUsed = null);

public record AssistantChatSummary(
    string ChatId,
    string Title,
    string Status,
    long? ActiveRunId,
    DateTime LastActivityAt);

public record AssistantRetentionCleanupRequest(
    DateTime NowUtc,
    DateTime? StaleActiveRunCutoffUtc,
    DateTime? TerminalRunCutoffUtc,
    DateTime? EventCutoffUtc,
    DateTime? ChatMessageCutoffUtc,
    DateTime? DebugExchangeCutoffUtc,
    DateTime? DebugTraceAuditCutoffUtc,
    int BatchSize);

public record AssistantRetentionCleanupResult(
    int StaleRunsFinalized,
    int RunsDeleted,
    int EventsDeleted,
    int ChatMessagesDeleted,
    int DebugExchangesDeleted,
    int DebugTraceAuditsDeleted)
{
    public int TotalRowsAffected =>
        StaleRunsFinalized +
        RunsDeleted +
        EventsDeleted +
        ChatMessagesDeleted +
        DebugExchangesDeleted +
        DebugTraceAuditsDeleted;
}

public interface IAssistantRunStore
{
    Task<AssistantRunCreateResult> CreateAssistantRunAsync(AssistantRunCreateRequest request, CancellationToken ct = default);
    Task UpdateAssistantRunStatusAsync(long runId, string status, string? finalAnswer = null,
        string? warningsJson = null, DateTime? completedAt = null, string? error = null,
        string? providerUsed = null, string? modelUsed = null);
    Task MarkAssistantRunCompletedAsync(long runId, string? finalAnswer = null, string? warningsJson = null,
        DateTime? completedAt = null, string? providerUsed = null, string? modelUsed = null);
    Task<AssistantRunEntity?> GetAssistantRunAsync(long runId);
    Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsByStatusAsync(IReadOnlyList<string> statuses);
    Task<AssistantRunEntity?> TryClaimAssistantRunAsync(long runId, string ownerId, DateTime leaseExpiresAt,
        CancellationToken ct = default);
    Task<AssistantRunLeaseRenewalResult> RenewAssistantRunLeaseAsync(long runId, string ownerId,
        DateTime leaseExpiresAt, CancellationToken ct = default);
    Task RequestAssistantRunCancellationAsync(long runId, string username, DateTime requestedAt,
        CancellationToken ct = default);
    Task SaveAssistantRunProgressAsync(long runId, AssistantRunProgressUpdate progress, CancellationToken ct = default);
    Task TransitionAssistantRunToTerminalAsync(long runId, AssistantRunTerminalUpdate update, CancellationToken ct = default);
    Task<AssistantRunEntity?> GetLatestAssistantRunAsync(string username, string chatId, CancellationToken ct = default);
    Task<IReadOnlyList<AssistantChatSummary>> GetAssistantChatSummariesAsync(string username, int take = 20,
        CancellationToken ct = default);
    Task<IReadOnlyList<AssistantChatMessageEntity>> GetAssistantChatMessagesAsync(
        string username,
        string chatId,
        long startMessageIndex = 0,
        long? endMessageIndex = null);
    Task AppendAssistantRunEventAsync(AssistantRunEventEntity evt);
    Task<IReadOnlyList<AssistantRunEventEntity>> GetAssistantRunEventsAsync(long runId, long afterSequence = 0,
        int? take = null);
    Task AppendAssistantDebugExchangeAsync(AssistantDebugExchangeEntity exchange, CancellationToken ct = default);
    Task<IReadOnlyList<AssistantDebugExchangeEntity>> GetAssistantDebugExchangesAsync(long runId,
        CancellationToken ct = default);
    Task AppendAssistantDebugTraceAuditAsync(AssistantDebugTraceAuditEntity audit, CancellationToken ct = default);
    Task<AssistantRetentionCleanupResult> CleanupAssistantRetentionAsync(
        AssistantRetentionCleanupRequest request,
        CancellationToken ct = default);
}
