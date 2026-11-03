namespace CodeGraph.Models.Memory;

/// <summary>
/// How far the memory scope reconciliation (CG-172) has scanned memory: the highest row id of each
/// table it matches repository names in. Row ids are auto-increment, so memory written later has
/// higher ids and the next reconciliation scans only the rows above this mark.
/// </summary>
public sealed record MemoryScopeWatermark(long EntityId, long ClaimId, long EvidenceId)
{
    public static MemoryScopeWatermark Zero { get; } = new(0, 0, 0);
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
