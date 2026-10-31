using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public interface IDreamingService
{
    Task<DreamRun> RunAsync(Guid projectId, string trigger, WorkActor initiator, CancellationToken ct = default);
    Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit = 30, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId, Guid? before, int limit = 30, CancellationToken ct = default);
    Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<DreamSettings> SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default);
    Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default);
    Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default);
}

public sealed record DreamToolCall(string Id, string Name, JsonElement Arguments);
public sealed record DreamModelMessage(string Role, string? Content = null, IReadOnlyList<DreamToolCall>? ToolCalls = null, string? ToolCallId = null);
public sealed record DreamToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record DreamModelRequest(string Provider, string Model, string System, IReadOnlyList<DreamModelMessage> Messages, IReadOnlyList<DreamToolDefinition> Tools, int MaxTokens);
public sealed record DreamModelResponse(string? Text, IReadOnlyList<DreamToolCall> ToolCalls, long InputTokens, long OutputTokens);
public interface IDreamBatchModel
{
    bool SupportsTools(string provider);
    Task<DreamModelResponse> CompleteAsync(DreamModelRequest request, CancellationToken ct = default);
    Task<IDreamPreparedTurn?> PrepareAsync(DreamModelRequest request, CancellationToken ct = default) => Task.FromResult<IDreamPreparedTurn?>(null);
}

/// <summary>An immutable provider/configuration/input snapshot shared by admission and generation.
/// The count is a provider estimate, not actual/billable usage or a guaranteed bound. Null means
/// unavailable; callers must independently admit the conservative fallback and charge actual usage.</summary>
public interface IDreamPreparedTurn
{
    Task<long?> CountInputTokensAsync(CancellationToken ct = default);
    Task<DreamModelResponse> CompleteAsync(int maxTokens, CancellationToken ct = default);
}
