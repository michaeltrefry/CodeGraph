using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Data;

public sealed record DreamKnowledgeDigest(JsonElement Data, string Fingerprint, bool HasSignal);
public sealed record DreamTrailReference(string Kind, string Id, Guid? ItemId, DateTime CreatedAt, string ContentHash, string? Preview = null, bool PreviewTruncated = false);
public sealed record DreamRestoreEntry(long JournalId, Guid EntryId, string Action, string Status, string? Reason = null);
public interface IDreamingKnowledgeStore
{
    Task<long?> LastDreamJournalIdAsync(Guid projectId, Guid runId, CancellationToken ct = default);
    Task<DreamKnowledgeDigest> ReadDreamDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default);
    Task<object> ReadDreamSimilarAsync(Guid projectId, int offset, int limit, CancellationToken ct = default);
    Task ValidateDreamEvidenceAsync(Guid projectId, IReadOnlyList<DreamEvidence> evidence, CancellationToken ct = default);
    Task<JsonElement> ReadDreamKnowledgeVersionAsync(Guid projectId, Guid entryId, long version, CancellationToken ct = default);
    Task<IReadOnlyList<DreamTrailReference>> ListDreamWorkTrailAsync(Guid projectId, DateTime? since, CancellationToken ct = default);
    async Task<IReadOnlyList<DreamTrailReference>> QueryDreamWorkTrailAsync(Guid projectId, DateTime? since, string? kind, string? id, Guid? itemId, CancellationToken ct = default) =>
        (await ListDreamWorkTrailAsync(projectId, since, ct)).Where(row => (kind is null || row.Kind == kind)
            && (id is null || row.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) && (itemId is null || row.ItemId == itemId)).ToList();
    Task<JsonElement> ReadDreamWorkTrailItemAsync(Guid projectId, DreamTrailReference reference, CancellationToken ct = default);
    Task<object> ReadDreamWorkTrailAsync(Guid projectId, DateTime? since, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRestoreEntry>> RevertDreamWritesAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default);
}
