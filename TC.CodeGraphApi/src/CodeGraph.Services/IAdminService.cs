using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public interface IAdminService
{
    Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request);
    Task<ProcessReposResponse> ReIndexAllAsync();
    Task LinkAsync(CancellationToken ct);
    Task DetectCommunitiesAsync(CancellationToken ct);
    Task LinkAndDetectAsync(CancellationToken ct);
    Task ProcessBatchAnalysisAsync(string? repo);
    Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request);
}
