using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public interface ICommunityDetectionService
{
    Task DetectCommunitiesAsync(CancellationToken ct = default);
    Task<ClusterOverviewResponse> GetClusterOverviewAsync();
    Task<ClusterDetailResponse?> GetClusterDetailAsync(int clusterId);
    Task<ClusterGraphResponse> GetClusterGraphAsync();
}
