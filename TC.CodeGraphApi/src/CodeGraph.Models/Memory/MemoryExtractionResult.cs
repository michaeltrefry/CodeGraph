using System.Text.Json.Serialization;

namespace CodeGraph.Models.Memory;

public class MemoryExtractionResult
{
    [JsonPropertyName("nodes")]
    public List<MemoryExtractedNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<MemoryExtractedEdge> Edges { get; set; } = [];
}

public class MemoryExtractedNode
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "unknown";

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public class MemoryExtractedEdge
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("relationship")]
    public required string Relationship { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("conflicts")]
    public bool Conflicts { get; set; }
}
