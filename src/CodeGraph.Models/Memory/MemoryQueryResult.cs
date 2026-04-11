namespace CodeGraph.Models.Memory;

public class MemoryQueryResult
{
    public List<MemoryEntityWithRelationships> Entities { get; set; } = [];
    public List<MemoryObservation> Conflicts { get; set; } = [];
    public MemorySubgraphResult? Subgraph { get; set; }
    public string FormattedText { get; set; } = string.Empty;
}

public class MemoryEntityWithRelationships
{
    public required MemoryEntity Entity { get; set; }
    public List<MemoryRelationshipDetail> Relationships { get; set; } = [];
    public double VectorScore { get; set; }
}

public class MemoryRelationshipDetail
{
    public required string Direction { get; set; }
    public required string RelationshipType { get; set; }
    public required string TargetLabel { get; set; }
    public required string TargetId { get; set; }
    public string? Context { get; set; }
    public DateTime Timestamp { get; set; }
    public float[]? Embedding { get; set; }
    public string? SourceId { get; set; }
}
