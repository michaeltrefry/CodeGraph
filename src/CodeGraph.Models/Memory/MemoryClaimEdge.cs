namespace CodeGraph.Models.Memory;

public class MemoryClaimEdge
{
    public required string FromClaimId { get; set; }
    public required string ToClaimId { get; set; }
    public required string EdgeType { get; set; }
    public decimal? Weight { get; set; }
    public required string Source { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
