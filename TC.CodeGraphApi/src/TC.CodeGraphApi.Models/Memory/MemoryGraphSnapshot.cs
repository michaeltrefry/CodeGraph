namespace TC.CodeGraphApi.Models.Memory;

public class MemoryGraphSnapshot
{
    public List<MemoryGraphNode> Nodes { get; set; } = [];
    public List<MemoryGraphLink> Links { get; set; } = [];
    public int TotalNodeCount { get; set; }
}

public class MemoryGraphNode
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public required string Type { get; set; }
    public required string Summary { get; set; }
}

public class MemoryGraphLink
{
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string Relationship { get; set; }
    public string? Context { get; set; }
    public DateTime Timestamp { get; set; }
}
