using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Data;

public sealed record DreamKnowledgeDigest(JsonElement Data, string Fingerprint, bool HasSignal);
public sealed record DreamRestoreEntry(long JournalId, Guid EntryId, string Action, string Status, string? Reason = null);
public interface IDreamingKnowledgeStore
{
    Task<DreamKnowledgeDigest> ReadDreamDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default);
    Task<object> ReadDreamWorkTrailAsync(Guid projectId, DateTime? since, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRestoreEntry>> RevertDreamWritesAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default);
}
