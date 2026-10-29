using CodeGraph.Models.Responses;
using CodeGraph.Services.Overlays;

namespace CodeGraph.Services.Query;

public interface INodeQueryService
{
    /// <summary>
    /// The read methods answer from <paramref name="scope"/> (a commit-pinned graph resolved by
    /// <see cref="ICommitScopeResolver"/>), or from the default-branch index when it is null, and
    /// name the commit they describe. A scope with <see cref="CommitScope.NotIndexed"/> must not be passed.
    /// </summary>
    Task<NodeDetailResponse?> GetDetailAsync(long id, CommitScope? scope = null);
    /// <summary>
    /// The read methods answer from <paramref name="scope"/> (a commit-pinned graph resolved by
    /// <see cref="ICommitScopeResolver"/>), or from the default-branch index when it is null, and
    /// name the commit they describe. A scope with <see cref="CommitScope.NotIndexed"/> must not be passed.
    /// </summary>
    Task<NodeListResponse> SearchAsync(string query, string? repository, string? label, int page, int pageSize, CommitScope? scope = null);
    Task<NodeSourceResponse?> GetNodeSourceAsync(long id, CommitScope? scope = null);
    Task<NodeByFileResponse?> FindNodeByFileAsync(string repository, string filePath, int? line = null, CommitScope? scope = null);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);
}
