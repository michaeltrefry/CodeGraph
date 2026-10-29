namespace CodeGraph.Models.Responses;

public record UnifiedSearchResponse(
    IReadOnlyList<SearchResultItem> Items,
    int Total,
    int Page,
    int PageSize);

public record SearchResultItem(
    string Type,          // "repository", "node"
    string Name,
    string? Description,
    string? NodeLabel,    // Only for nodes: "Class", "Method", etc.
    string? Repository,      // Repository name this belongs to
    long? NodeId,         // Only for nodes
    string? QualifiedName);
