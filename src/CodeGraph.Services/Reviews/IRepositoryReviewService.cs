using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Reviews;

public interface IRepositoryReviewService
{
    Task<long> StartReviewAsync(string repo, string mode, string? requestedBy = null, CancellationToken ct = default);
    Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default);
    Task<RepositoryReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default);
    Task<RepositoryReviewResponse?> GetLatestReviewAsync(string repo, CancellationToken ct = default);
    /// <summary>Newest-first page of a repository's runs (id &lt; <paramref name="before"/>); null when the repository is unknown.</summary>
    Task<RepositoryReviewRunPage?> ListReviewsAsync(string repo, int limit, long? before = null, CancellationToken ct = default);
}
