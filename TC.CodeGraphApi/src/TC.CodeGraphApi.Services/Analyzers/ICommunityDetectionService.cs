using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Analyzers;

public interface ICommunityDetectionService
{
    Task DetectCommunitiesAsync(CancellationToken ct = default);
    Task<ClusterOverviewResponse> GetClusterOverviewAsync();
    Task<ClusterDetailResponse?> GetClusterDetailAsync(int clusterId);
    Task<ClusterGraphResponse> GetClusterGraphAsync();
}
