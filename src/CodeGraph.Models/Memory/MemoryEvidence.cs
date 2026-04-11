namespace CodeGraph.Models.Memory;

public class MemoryEvidence
{
    public required string Id { get; set; }
    public string? ClaimId { get; set; }
    public string? ObservationId { get; set; }
    public required string EvidenceType { get; set; }
    public required string SourceRef { get; set; }
    public string? Snippet { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
