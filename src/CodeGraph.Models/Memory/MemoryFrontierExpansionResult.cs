namespace CodeGraph.Models.Memory;

public class MemoryFrontierExpansionResult
{
    public List<MemorySubgraphEntity> AddedEntities { get; set; } = [];
    public List<MemorySubgraphClaim> AddedClaims { get; set; } = [];
    public List<MemoryPathExplanation> Paths { get; set; } = [];
    public MemoryFrontierExpansionMeta Meta { get; set; } = new();
}

public class MemoryFrontierExpansionMeta
{
    public int AdditionalHopsUsed { get; set; }
    public int FrontierExpanded { get; set; }
    public bool ResponseTruncated { get; set; }
}
