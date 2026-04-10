namespace CodeGraph.Models.Memory;

public class MemoryEntityEdge
{
    public required string FromEntityId { get; set; }
    public required string ToEntityId { get; set; }
    public required string EdgeType { get; set; }
    public string? BestActiveClaimId { get; set; }
    public decimal? Weight { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
