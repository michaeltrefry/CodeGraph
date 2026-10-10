using CodeGraph.Models.Responses;
using CodeGraph.Services.Overlays;

namespace CodeGraph.Services.Query;

public interface INodeQueryService
{
    Task<NodeDetailResponse?> GetDetailAsync(long id);
    /// <summary>
    /// The read methods answer from <paramref name="scope"/> (a commit-pinned graph resolved by
    /// <see cref="ICommitScopeResolver"/>), or from the default-branch index when it is null, and
    /// name the commit they describe. A scope with <see cref="CommitScope.NotIndexed"/> must not be passed.
    /// </summary>
    Task<NodeListResponse> SearchAsync(string query, string? project, string? label, int page, int pageSize, CommitScope? scope = null);
    Task<NodeSourceResponse?> GetNodeSourceAsync(long id, CommitScope? scope = null);
    Task<NodeByFileResponse?> FindNodeByFileAsync(string project, string filePath, int? line = null, CommitScope? scope = null);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);
}
