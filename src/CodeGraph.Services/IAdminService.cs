using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public interface IAdminService
{
    Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request, CancellationToken ct = default);
    Task<ProcessReposResponse> ReIndexAllAsync(CancellationToken ct = default);
    Task LinkAsync(CancellationToken ct);
    Task DetectCommunitiesAsync(CancellationToken ct);
    Task LinkAndDetectAsync(CancellationToken ct);
    Task ProcessBatchAnalysisAsync(string? repo, CancellationToken ct = default);
    Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request, CancellationToken ct = default);
}
