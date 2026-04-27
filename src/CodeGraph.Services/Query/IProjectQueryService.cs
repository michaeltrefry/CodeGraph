using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Query;

public interface IProjectQueryService
{
    Task<ProjectListResponse> ListAsync(string? search, string? group, int page, int pageSize);
    Task<SchemaListResponse> ListSchemasAsync(string? search, string? server, string? database, int page, int pageSize);
    Task<SchemaCatalogResponse?> GetSchemaCatalogAsync(string name);
    Task<ProjectDetailResponse?> GetDetailAsync(string name);
    Task<ProjectHealthResponse?> GetHealthAsync(string name);
    Task<IReadOnlyList<FileMetrics>> GetMetricsAsync(string name, string? dotnetProject, int top);
    Task<IReadOnlyList<FileMetrics>> GetHotspotsAsync(string name, int top);
    Task<NodeListResponse> GetNodesAsync(string name, string? label, string? dotnetProject, int page, int pageSize);
    Task<AnalysisBatchResponse?> GetBatchStatusAsync(string name);
    Task<ProjectSecurityResponse?> GetSecurityAsync(string name);
    Task<string?> GetReadmeAsync(string name);
}
