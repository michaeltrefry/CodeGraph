namespace CodeGraph.Models.Memory;

public class MemorySubgraphRequest
{
    public string? Query { get; set; }
    public List<string> SeedEntityIds { get; set; } = [];
    public List<string> SeedClaimIds { get; set; } = [];
    public int MaxHops { get; set; } = 2;
    public int MaxReturnedEntities { get; set; } = 20;
    public int MaxReturnedClaims { get; set; } = 40;
    public bool IncludeSuperseded { get; set; }
    public bool IncludeConflicts { get; set; } = true;
}
