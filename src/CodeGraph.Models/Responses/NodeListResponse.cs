namespace CodeGraph.Models.Responses;

public record NodeListResponse(
    IReadOnlyList<GraphNode> Items,
    int Total,
    int Page,
    int PageSize)
{
    /// <summary>The commit this answer describes (the default-branch index commit, or the requested SHA).</summary>
    public string? CommitSha { get; init; }
}
