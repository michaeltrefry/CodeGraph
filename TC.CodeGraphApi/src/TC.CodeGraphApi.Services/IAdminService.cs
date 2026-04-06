using TC.CodeGraphApi.Models.Requests;
using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services;

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
