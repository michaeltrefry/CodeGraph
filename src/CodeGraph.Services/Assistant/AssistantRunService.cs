using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Assistant;

public sealed class AssistantRunService(
    IAssistantRunStore store,
    GraphAssistant assistant,
    IAssistantRunBackgroundRunner backgroundRunner,
    IAssistantDebugCapture debugCapture,
    ILogger<AssistantRunService> logger) : IAssistantRunService
{
    private static readonly JsonSerializerOptions JsonOptions = CodeGraphJsonDefaults.CamelCase;
    private const string CancelledMessage = "Run cancelled by user.";

    public async Task<AssistantRunStartResult> CreateRunAsync(
        AskRequest request,
        string username,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var chatId = string.IsNullOrWhiteSpace(request.ChatId)
            ? Guid.NewGuid().ToString("D")
            : request.ChatId.Trim();

        var createResult = await store.CreateAssistantRunAsync(
            new AssistantRunCreateRequest(
                chatId,
                normalizedUsername,
                request.Question,
                request.Context,
                request.History ?? [],
                request.Provider?.Trim(),
                request.Model?.Trim(),
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
                ComputeRequestHash(request),
                DateTime.UtcNow),
            ct);

        if (createResult.Conflict is not null)
            throw new AssistantRunConflictException(createResult.Conflict);

        var run = createResult.Run
            ?? throw new InvalidOperationException("Assistant run creation returned no run.");

        if (!createResult.ReusedExisting)
            await backgroundRunner.EnqueueAsync(run.Id, CancellationToken.None);

        return new AssistantRunStartResult(MapRun(run), createResult.ReusedExisting);
    }

    public async Task<AssistantRunResponse?> CancelRunAsync(long runId, string username, CancellationToken ct = default)
    {
        var run = await store.GetAssistantRunAsync(runId);
        if (run is null || !OwnsRun(run, username))
            return null;

        if (IsTerminalStatus(run.Status))
            return MapRun(run);

        await store.RequestAssistantRunCancellationAsync(runId, NormalizeUsername(username), DateTime.UtcNow, ct);
        var cancellationRequested = await backgroundRunner.CancelAsync(runId, ct);
        if (!cancellationRequested)
            logger.LogWarning("Assistant cancellation requested for run {RunId}, but no active background runner was found.", runId);

        var refreshedRun = await store.GetAssistantRunAsync(runId) ?? run;
        return MapRun(refreshedRun);
    }

    public async Task ExecuteRunAsync(long runId, CancellationToken ct = default)
    {
        var run = await store.GetAssistantRunAsync(runId)
            ?? throw new InvalidOperationException($"Assistant run '{runId}' was not found.");

        if (IsTerminalStatus(run.Status))
            return;

        var lastSequence = run.LastSequence;
        var events = new List<AssistantRunEventEntity>();
        var finalAnswer = new StringBuilder();
        var startedAt = DateTime.UtcNow;

        try
        {
            await store.UpdateAssistantRunStatusAsync(runId, "running");
            events.Add(CreateEvent(runId, ++lastSequence, "status", new { status = "running" }, startedAt));
            await store.SaveAssistantRunProgressAsync(runId, new AssistantRunProgressUpdate(events), ct);
            events.Clear();

            var history = await LoadHistoryAsync(run);
            using var debugScope = debugCapture.BeginRun(new AssistantDebugRunContext(run.Id, run.ChatId, run.Username));
            await foreach (var evt in assistant.AskAsync(
                run.Question,
                run.Context,
                history,
                run.Username,
                ct))
            {
                if (evt.Type == "text")
                    finalAnswer.Append(evt.Content);

                events.Add(CreateEvent(runId, ++lastSequence, evt.Type, evt.Content, DateTime.UtcNow));
                await store.SaveAssistantRunProgressAsync(runId, new AssistantRunProgressUpdate(
                    events,
                    BuildExecutionStateJson(run, lastSequence, finalAnswer.Length, evt.Type, startedAt)),
                    ct);
                events.Clear();
            }

            await store.TransitionAssistantRunToTerminalAsync(
                runId,
                new AssistantRunTerminalUpdate(
                    "completed",
                    [CreateEvent(runId, ++lastSequence, "completed", new { status = "completed" }, DateTime.UtcNow)],
                    FinalAnswer: finalAnswer.ToString(),
                    CompletedAt: DateTime.UtcNow),
                ct);
        }
        catch (OperationCanceledException)
        {
            await store.TransitionAssistantRunToTerminalAsync(
                runId,
                new AssistantRunTerminalUpdate(
                    "cancelled",
                    [CreateEvent(runId, ++lastSequence, "cancelled", CancelledMessage, DateTime.UtcNow)],
                    FinalAnswer: finalAnswer.Length == 0 ? null : finalAnswer.ToString(),
                    CompletedAt: DateTime.UtcNow,
                    Error: CancelledMessage),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Assistant run {RunId} failed", runId);
            await store.TransitionAssistantRunToTerminalAsync(
                runId,
                new AssistantRunTerminalUpdate(
                    "failed",
                    [CreateEvent(runId, ++lastSequence, "error", ex.Message, DateTime.UtcNow)],
                    FinalAnswer: finalAnswer.Length == 0 ? null : finalAnswer.ToString(),
                    CompletedAt: DateTime.UtcNow,
                    Error: ex.Message),
                CancellationToken.None);
        }
    }

    public async Task<AssistantRunResponse?> GetRunAsync(long runId, string username, CancellationToken ct = default)
    {
        var run = await store.GetAssistantRunAsync(runId);
        if (run is null || !OwnsRun(run, username))
            return null;

        return MapRun(run);
    }

    public async Task<AssistantRunEventsResponse?> GetEventsAsync(
        long runId,
        long afterSequence,
        string username,
        CancellationToken ct = default)
    {
        var run = await store.GetAssistantRunAsync(runId);
        if (run is null || !OwnsRun(run, username))
            return null;

        var events = await store.GetAssistantRunEventsAsync(runId, afterSequence);
        return new AssistantRunEventsResponse(
            MapRun(run),
            events.Select(MapEvent).ToList());
    }

    public async Task<AssistantDebugExchangeListResponse?> GetDebugExchangesAsync(
        long runId,
        string username,
        string? remoteIp = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        var run = await store.GetAssistantRunAsync(runId);
        if (run is null || !OwnsRun(run, username))
            return null;

        var viewer = NormalizeUsername(username);
        await store.AppendAssistantDebugTraceAuditAsync(new AssistantDebugTraceAuditEntity
        {
            RunId = run.Id,
            ChatId = run.ChatId,
            RunUsername = run.Username,
            ViewedByUsername = viewer,
            RemoteIp = string.IsNullOrWhiteSpace(remoteIp) ? null : remoteIp.Trim(),
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim(),
            ViewedAt = DateTime.UtcNow
        }, ct);

        var exchanges = await store.GetAssistantDebugExchangesAsync(runId, ct);
        return new AssistantDebugExchangeListResponse(
            MapRun(run),
            exchanges.Select(MapDebugExchange).ToList());
    }

    public async Task<IReadOnlyList<AssistantChatSummaryResponse>> ListChatsAsync(
        string username,
        int take = 20,
        CancellationToken ct = default)
    {
        var chats = await store.GetAssistantChatSummariesAsync(NormalizeUsername(username), take, ct);
        return chats
            .Select(summary => new AssistantChatSummaryResponse(
                summary.ChatId,
                summary.Title,
                summary.Status,
                summary.ActiveRunId,
                summary.LastActivityAt))
            .ToList();
    }

    public async Task<AssistantChatTranscriptResponse?> GetChatAsync(
        string chatId,
        string username,
        CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var latestRun = await store.GetLatestAssistantRunAsync(normalizedUsername, chatId, ct);
        if (latestRun is null)
            return null;

        var messages = await store.GetAssistantChatMessagesAsync(normalizedUsername, chatId);
        return new AssistantChatTranscriptResponse(
            latestRun.ChatId,
            BuildChatTitle(messages, latestRun.Question),
            messages.Select(MapChatMessage).ToList(),
            latestRun.Status is "queued" or "running" ? MapRun(latestRun) : null,
            latestRun.CompletedAt ?? latestRun.StartedAt ?? latestRun.CreatedAt);
    }

    private static AssistantRunEventEntity CreateEvent(long runId, long sequence, string type, object? content, DateTime createdAt) => new()
    {
        RunId = runId,
        Sequence = sequence,
        Type = type,
        ContentJson = content is null ? null : JsonSerializer.Serialize(content, JsonOptions),
        CreatedAt = createdAt
    };

    private static AssistantRunResponse MapRun(AssistantRunEntity run) => new(
        run.Id,
        run.ChatId,
        run.Username,
        run.Status,
        run.Question,
        run.Context,
        run.ProviderRequested,
        run.ModelRequested,
        run.ProviderUsed,
        run.ModelUsed,
        run.FinalAnswer,
        DeserializeWarnings(run.WarningsJson),
        run.Error,
        run.LastSequence,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt);

    private static AssistantRunEventResponse MapEvent(AssistantRunEventEntity evt) => new(
        evt.Sequence,
        evt.Type,
        DeserializeContent(evt.ContentJson),
        evt.CreatedAt);

    private static AssistantDebugExchangeResponse MapDebugExchange(AssistantDebugExchangeEntity exchange) => new(
        exchange.ExchangeIndex,
        exchange.TurnIndex,
        exchange.Provider,
        exchange.Model,
        DeserializeDebugJson(exchange.RequestBodyJson) ?? new { },
        DeserializeDebugJson(exchange.ResponseBodyJson),
        exchange.RequestText,
        exchange.ResponseText,
        DeserializeDebugJson(exchange.ToolUsesJson),
        DeserializeDebugJson(exchange.RequestMetadataJson),
        DeserializeDebugJson(exchange.ResponseMetadataJson),
        exchange.RequestId,
        exchange.ResponseId,
        exchange.InputTokens,
        exchange.OutputTokens,
        exchange.TotalTokens,
        exchange.CreatedAt);

    private static AssistantChatMessageResponse MapChatMessage(AssistantChatMessageEntity message) => new(
        message.MessageIndex,
        message.Role,
        message.Content,
        message.SourceRunId,
        message.CreatedAt);

    private static List<string> DeserializeWarnings(string? warningsJson)
    {
        if (string.IsNullOrWhiteSpace(warningsJson))
            return [];

        return JsonSerializer.Deserialize<List<string>>(warningsJson, JsonOptions) ?? [];
    }

    private static object? DeserializeContent(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
            return null;

        var element = JsonSerializer.Deserialize<JsonElement>(contentJson, JsonOptions);
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element;
    }

    private static object? DeserializeDebugJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
    }

    private static string BuildExecutionStateJson(
        AssistantRunEntity run,
        long lastSequence,
        int finalAnswerLength,
        string lastEventType,
        DateTime startedAt) =>
        JsonSerializer.Serialize(new
        {
            runId = run.Id,
            run.ChatId,
            run.Username,
            status = "running",
            lastSequence,
            finalAnswerLength,
            lastEventType,
            startedAt,
            updatedAt = DateTime.UtcNow
        }, JsonOptions);

    private static bool OwnsRun(AssistantRunEntity run, string username) =>
        string.Equals(run.Username, NormalizeUsername(username), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "anonymous" : username.Trim().ToLowerInvariant();

    private static bool IsTerminalStatus(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";

    private static string BuildChatTitle(IReadOnlyList<AssistantChatMessageEntity> messages, string fallbackQuestion)
    {
        var title = messages
            .FirstOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content;
        return string.IsNullOrWhiteSpace(title) ? fallbackQuestion : title;
    }

    private async Task<IReadOnlyList<ChatMessage>?> LoadHistoryAsync(AssistantRunEntity run)
    {
        if (run.MessageIndexEnd <= run.MessageIndexStart)
            return null;

        var messages = await store.GetAssistantChatMessagesAsync(
            run.Username,
            run.ChatId,
            run.MessageIndexStart,
            run.MessageIndexEnd);

        return messages
            .Select(message => new ChatMessage(message.Role, message.Content))
            .ToList();
    }

    private static string ComputeRequestHash(AskRequest request)
    {
        var json = JsonSerializer.Serialize(new
        {
            request.Question,
            request.Context,
            request.History,
            request.Provider,
            request.Model,
            request.ChatId
        }, JsonOptions);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
