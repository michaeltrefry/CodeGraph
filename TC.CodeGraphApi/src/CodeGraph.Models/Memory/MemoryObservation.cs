namespace CodeGraph.Models.Memory;

public class MemoryObservation
{
    public required string Id { get; set; }
    public required string Claim { get; set; }
    public required string ConflictsWith { get; set; }
    public required string Source { get; set; }
    public required string Username { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Resolved { get; set; }
    public string? Resolution { get; set; }
    public string? ResolvedByMemoryId { get; set; }
}
