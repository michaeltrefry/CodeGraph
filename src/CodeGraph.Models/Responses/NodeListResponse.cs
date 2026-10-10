namespace CodeGraph.Models.Responses;

public record NodeListResponse(
    IReadOnlyList<GraphNode> Items,
    int Total,
    int Page,
    int PageSize)
{
    /// <summary>The commit this answer describes (the default-branch index commit, or the requested SHA).</summary>
    public string? CommitSha { get; init; }

    /// <summary>
    /// How many of <see cref="Items"/> are low-confidence tree-sitter fallback nodes
    /// (<see cref="GraphNodeConfidence.IsLowConfidence"/>).
    /// </summary>
    public int LowConfidenceCount => Items.Count(GraphNodeConfidence.IsLowConfidence);

    /// <summary>True when the page includes low-confidence nodes.</summary>
    public bool LowConfidence => LowConfidenceCount > 0;
}
