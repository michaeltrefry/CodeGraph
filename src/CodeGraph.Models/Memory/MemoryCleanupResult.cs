namespace CodeGraph.Models.Memory;

public sealed class MemoryCleanupResult
{
    public string Username { get; init; } = "default";
    public required string Scope { get; init; }
    public bool DryRun { get; init; }
    public bool NoOp { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> ClaimIds { get; init; } = [];
    public IReadOnlyList<string> EntityIds { get; init; } = [];
    public int FactGroupsAffected { get; init; }
    public int ClaimsDeleted { get; init; }
    public int EntitiesDeleted { get; init; }
    public int OrphanEntitiesDeleted { get; init; }
    public int ClaimEdgesDeleted { get; init; }
    public int ClaimEdgesRebuilt { get; init; }
    public int EntityEdgesDeleted { get; init; }
    public int EntityEdgesRebuilt { get; init; }
    public int AdjacencyDeleted { get; init; }
    public int AdjacencyRebuilt { get; init; }
    public int ActiveClaimsDeleted { get; init; }
    public int ActiveClaimsRebuilt { get; init; }
    public int AliasesDeleted { get; init; }
    public int AliasesRebuilt { get; init; }
    public int EvidenceDeleted { get; init; }
    public int ObservationsDeleted { get; init; }
    public int ObservationsRebuilt { get; init; }
    public int WriteReceiptsDeleted { get; init; }
}
