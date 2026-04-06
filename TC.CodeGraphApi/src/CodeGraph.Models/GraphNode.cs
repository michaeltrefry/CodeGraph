namespace CodeGraph.Models;

public record GraphNode
{
    public long Id { get; init; }
    public required string Project { get; init; }
    public string? DotnetProject { get; init; }
    public required NodeLabel Label { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
    public bool DoNotTrust { get; init; }

    // Populated on demand from node_analysis — null if not yet analyzed
    public string? Description { get; init; }
    public string? AnalysisConfidence { get; init; }
}
