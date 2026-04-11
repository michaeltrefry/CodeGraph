namespace CodeGraph.Models.Memory;

public class MemoryClaim
{
    public required string Id { get; set; }
    public required string ClaimKey { get; set; }
    public required string FactGroupKey { get; set; }
    public required string SubjectEntityId { get; set; }
    public required string Predicate { get; set; }
    public string? ObjectEntityId { get; set; }
    public string? ValueText { get; set; }
    public string? ValueJson { get; set; }
    public required string NormalizedText { get; set; }
    public MemoryClaimStatus Status { get; set; } = MemoryClaimStatus.Active;
    public decimal? Confidence { get; set; }
    public DateTime? EffectiveAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public string? SupersedesClaimId { get; set; }
    public required string Source { get; set; }
    public float[]? Embedding { get; set; }
}

public enum MemoryClaimStatus
{
    Active,
    Superseded,
    Conflicted,
    Deprecated,
}
