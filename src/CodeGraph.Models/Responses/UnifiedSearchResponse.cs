namespace CodeGraph.Models.Responses;

public record UnifiedSearchResponse(
    IReadOnlyList<SearchResultItem> Items,
    int Total,
    int Page,
    int PageSize);

public record SearchResultItem(
    string Type,          // "repository", "project", "node"
    string Name,
    string? Description,
    string? NodeLabel,    // Only for nodes: "Class", "Method", etc.
    string? Project,      // Repo/project name this belongs to
    long? NodeId,         // Only for nodes
    string? QualifiedName);
