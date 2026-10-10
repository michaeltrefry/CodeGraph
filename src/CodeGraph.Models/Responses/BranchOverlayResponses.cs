namespace CodeGraph.Models.Responses;

/// <summary>
/// Status of a branch overlay index. <see cref="Status"/> is one of
/// <c>queued</c>, <c>indexing</c>, <c>ready</c>, <c>failed</c>, or <c>expired</c>.
/// <see cref="BaseSha"/> is the default-branch index commit the overlay was diffed against.
/// </summary>
public sealed record BranchOverlayResponse(
    long OverlayId,
    string Repo,
    string Ref,
    string Status,
    string? HeadSha,
    string? BaseSha,
    string? Error,
    long? RunId,
    int ChangedFileCount,
    int NodeCount,
    int EdgeCount,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime ExpiresAt,
    DateTime? ExpiredAt,
    string StatusUrl);
