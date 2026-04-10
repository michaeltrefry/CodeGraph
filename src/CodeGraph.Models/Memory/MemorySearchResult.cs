namespace CodeGraph.Models.Memory;

public class MemorySearchResult
{
    public string Query { get; set; } = string.Empty;
    public List<MemoryEntitySeed> Entities { get; set; } = [];
    public List<MemoryClaimSeed> Claims { get; set; } = [];
}

public class MemoryEntitySeed
{
    public required string EntityId { get; set; }
    public required string Label { get; set; }
    public required string Type { get; set; }
    public double Score { get; set; }
    public string MatchKind { get; set; } = string.Empty;
}

public class MemoryClaimSeed
{
    public required string ClaimId { get; set; }
    public required string NormalizedText { get; set; }
    public required string Predicate { get; set; }
    public MemoryClaimStatus Status { get; set; } = MemoryClaimStatus.Active;
    public double Score { get; set; }
    public string MatchKind { get; set; } = string.Empty;
}
