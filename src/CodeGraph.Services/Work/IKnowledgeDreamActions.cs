using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed record DreamDigest(JsonElement Data, string Fingerprint, bool HasSignal);
public sealed record DreamRevertEntry(long JournalId, Guid EntryId, string Action, string Status, string? Reason = null);
public sealed record DreamRevertResult(Guid RunId, IReadOnlyList<DreamRevertEntry> Entries);
public interface IKnowledgeDreamActions
{
    JsonElement CaptureReadWindows(DreamExecutionScope scope)
    {
        if (scope is null || !ReferenceEquals(scope, DreamExecutionScope.Current)) throw new WorkException(WorkErrorCodes.Forbidden, "Read windows require the active execution scope.");
        return JsonSerializer.SerializeToElement(new { });
    }
    void RestoreReadWindows(DreamExecutionScope scope, JsonElement state)
    {
        if (scope is null || !ReferenceEquals(scope, DreamExecutionScope.Current)) throw new WorkException(WorkErrorCodes.Forbidden, "Read windows require the active execution scope.");
        if (state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || state.ValueKind == JsonValueKind.Object && !state.EnumerateObject().Any()) return;
        throw WorkException.Invalid("This knowledge reader cannot restore nonempty read-window state.");
    }
    Task<DreamDigest> BuildDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default);
    Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default);
    Task<DreamRevertResult> RevertAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default);
}
