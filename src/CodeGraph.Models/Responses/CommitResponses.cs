namespace CodeGraph.Models.Responses;

/// <summary>
/// Typed answer for a commit-pinned query (<c>sha</c>) that cannot be answered for that commit.
/// It never carries default-branch data. <see cref="Code"/> is <c>commit_not_indexed</c> (no ready
/// overlay; <see cref="Status"/> is the overlay status <c>queued</c>, <c>indexing</c>, <c>failed</c>,
/// <c>expired</c>, <c>stale</c> when the default-branch index moved past the overlay's base, or
/// <c>unknown</c> when no overlay exists), <c>invalid_sha</c> (not a full 40/64-hex SHA), or
/// <c>ambiguous_sha</c> (overlays for the SHA exist in several repositories; pass the repo).
/// </summary>
public sealed record CommitNotIndexedResponse(
    string Code,
    string? Repo,
    string Sha,
    string Status,
    string Message,
    long? OverlayId = null,
    string? BaseSha = null);

/// <summary>Response of <c>GET /api/nodes/by-file</c>.</summary>
public sealed record NodeByFileResponse(long NodeId, string? CommitSha);
