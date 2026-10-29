namespace CodeGraph.Models.Responses;

public record RepositoryListResponse(
    IReadOnlyList<RepositoryListItem> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Groups);

public record RepositoryListItem(
    string Name,
    string? RepoUrl,
    string? SourceGroup,
    string? LocalPath,
    string? LastCommitSha,
    DateTime? IndexedAt,
    string? Language,
    string? Framework,
    bool IsFoundational,
    Dictionary<string, object>? Properties);
