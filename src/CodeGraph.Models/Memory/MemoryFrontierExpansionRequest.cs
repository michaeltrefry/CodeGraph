namespace CodeGraph.Models.Memory;

public class MemoryFrontierExpansionRequest
{
    public List<string> FrontierEntityIds { get; set; } = [];
    public List<string> FrontierClaimIds { get; set; } = [];
    public int MaxAdditionalHops { get; set; } = 2;
    public int FrontierLimit { get; set; } = 20;
    public double MinScore { get; set; }
}
