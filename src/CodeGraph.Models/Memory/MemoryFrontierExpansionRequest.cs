namespace CodeGraph.Models.Memory;

public class MemoryFrontierExpansionRequest
{
    public List<string> FrontierEntityIds { get; set; } = [];
    public List<string> FrontierClaimIds { get; set; } = [];
    public int MaxAdditionalHops { get; set; } = 2;
    public int FrontierLimit { get; set; } = 20;
    public double MinScore { get; set; }
    // The key of a work project (CG-165): only its entities and claims are expanded to. Null: unscoped.
    public string? ProjectKey { get; set; }
}
