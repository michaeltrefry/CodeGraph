namespace CodeGraph.Models.Responses;

public record NodeListResponse(
    IReadOnlyList<GraphNode> Items,
    int Total,
    int Page,
    int PageSize);
