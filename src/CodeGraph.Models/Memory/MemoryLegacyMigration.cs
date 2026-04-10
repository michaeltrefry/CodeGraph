namespace CodeGraph.Models.Memory;

public class MemoryLegacyRelationship
{
    public required string FromEntityId { get; set; }
    public required string ToEntityId { get; set; }
    public required string RelationshipType { get; set; }
    public string? Context { get; set; }
    public string? Source { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Supersedes { get; set; }
}

public class MemoryLegacyMigrationResult
{
    public string MigrationSource { get; set; } = string.Empty;
    public int LegacyRelationshipsRead { get; set; }
    public int LegacyRelationshipsSkipped { get; set; }
    public int LegacyRelationshipsWithContext { get; set; }
    public StoreMemoryResult StoreResult { get; set; } = new();
}
