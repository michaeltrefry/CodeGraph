using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Assistant;

public record AssistantRunStartResult(
    AssistantRunResponse Run,
    bool ReusedExisting = false);

public interface IAssistantRunService
{
    Task<AssistantRunStartResult> CreateRunAsync(
        AskRequest request,
        string username,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    Task<AssistantRunResponse?> CancelRunAsync(long runId, string username, CancellationToken ct = default);
    Task ExecuteRunAsync(long runId, CancellationToken ct = default);
    Task<AssistantRunResponse?> GetRunAsync(long runId, string username, CancellationToken ct = default);
    Task<AssistantRunEventsResponse?> GetEventsAsync(long runId, long afterSequence, string username, CancellationToken ct = default);
    Task<AssistantDebugExchangeListResponse?> GetDebugExchangesAsync(
        long runId,
        string username,
        string? remoteIp = null,
        string? userAgent = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<AssistantChatSummaryResponse>> ListChatsAsync(string username, int take = 20, CancellationToken ct = default);
    Task<AssistantChatTranscriptResponse?> GetChatAsync(string chatId, string username, CancellationToken ct = default);
}
