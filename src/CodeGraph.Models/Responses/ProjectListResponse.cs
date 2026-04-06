namespace CodeGraph.Models.Responses;

public record ProjectListResponse(
    IReadOnlyList<ProjectListItem> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Groups);

public record ProjectListItem(
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
