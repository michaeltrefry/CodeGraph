namespace CodeGraph.Models.Memory;

public class MemorySubgraphResult
{
    public MemorySubgraphQuery Query { get; set; } = new();
    public MemorySubgraphSeeds Seeds { get; set; } = new();
    public List<MemorySubgraphEntity> Entities { get; set; } = [];
    public List<MemorySubgraphClaim> Claims { get; set; } = [];
    public List<MemoryEntityEdge> EntityEdges { get; set; } = [];
    public List<MemoryClaimEdge> ClaimEdges { get; set; } = [];
    public List<MemoryObservation> Observations { get; set; } = [];
    public List<MemoryPathExplanation> Paths { get; set; } = [];
    public MemorySubgraphMeta Meta { get; set; } = new();
}

public class MemorySubgraphQuery
{
    public string Text { get; set; } = string.Empty;
    public List<string> SeedEntityIds { get; set; } = [];
    public List<string> SeedClaimIds { get; set; } = [];
}

public class MemorySubgraphSeeds
{
    public List<MemoryEntitySeed> Entities { get; set; } = [];
    public List<MemoryClaimSeed> Claims { get; set; } = [];
}

public class MemorySubgraphEntity
{
    public required MemoryEntity Entity { get; set; }
    public double Score { get; set; }
    public int HopDistance { get; set; }
    public bool IsDirectSeed { get; set; }
}

public class MemorySubgraphClaim
{
    public required MemoryClaim Claim { get; set; }
    public double Score { get; set; }
    public int HopDistance { get; set; }
    public bool IsDirectSeed { get; set; }
}

public class MemorySubgraphMeta
{
    public int MaxHopsUsed { get; set; }
    public int FrontierExpanded { get; set; }
    public int ActiveClaimsHidden { get; set; }
    public int SupersededClaimsHidden { get; set; }
    public bool ResponseTruncated { get; set; }
}
