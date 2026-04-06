using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Query;

public interface INodeQueryService
{
    Task<NodeDetailResponse?> GetDetailAsync(long id);
    Task<NodeListResponse> SearchAsync(string query, string? project, string? label, int page, int pageSize);
    Task<NodeSourceResponse?> GetNodeSourceAsync(long id);
    Task<long?> FindNodeByFileAsync(string project, string filePath, int? line = null);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);
}
