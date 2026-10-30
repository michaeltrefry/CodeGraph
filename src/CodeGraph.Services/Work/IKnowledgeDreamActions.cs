using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed record DreamDigest(JsonElement Data, string Fingerprint, bool HasSignal);
public sealed record DreamRevertEntry(long JournalId, Guid EntryId, string Action, string Status, string? Reason = null);
public sealed record DreamRevertResult(Guid RunId, IReadOnlyList<DreamRevertEntry> Entries);
public interface IKnowledgeDreamActions
{
    Task<DreamDigest> BuildDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default);
    Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default);
    Task<DreamRevertResult> RevertAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default);
}
