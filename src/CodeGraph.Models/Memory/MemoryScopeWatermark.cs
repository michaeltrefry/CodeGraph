namespace CodeGraph.Models.Memory;

/// <summary>
/// How far the memory scope reconciliation (CG-172) has scanned memory, per table it matches
/// repository names in: the highest change mark (scope_seq, migration 117) of entities and claims, which
/// is taken anew when a row is inserted or rewritten in a way that changes what it names, and the
/// highest evidence id (evidence is only inserted). Memory written or rewritten later carries a higher
/// mark, so the next reconciliation scans only the rows above this one.
/// </summary>
public sealed record MemoryScopeWatermark(long EntitySeq, long ClaimSeq, long EvidenceId)
{
    public static MemoryScopeWatermark Zero { get; } = new(0, 0, 0);

    /// <summary>True when any mark is above <paramref name="other"/>'s: this watermark is ahead of that memory.</summary>
    public bool IsAheadOf(MemoryScopeWatermark other) =>
        EntitySeq > other.EntitySeq || ClaimSeq > other.ClaimSeq || EvidenceId > other.EvidenceId;
}

/// <summary>Asks the memory host for its memory watermark (CG-172): the current one, or the settled rows above <see cref="Since"/>.</summary>
public sealed class MemoryProjectScopeWatermarkRequest
{
    public MemoryScopeWatermark? Since { get; init; }
}

/// <summary>
/// Asks the memory host to scope the memory written between two watermarks (after <see cref="After"/>,
/// up to and including <see cref="UpTo"/>) to every listed project whose repositories it names (CG-172).
/// </summary>
public sealed class MemoryProjectScopeRangeBackfillRequest
{
    public IReadOnlyList<MemoryProjectScopeBackfillRequest> Projects { get; init; } = [];
    public MemoryScopeWatermark After { get; init; } = MemoryScopeWatermark.Zero;
    public MemoryScopeWatermark UpTo { get; init; } = MemoryScopeWatermark.Zero;
}
