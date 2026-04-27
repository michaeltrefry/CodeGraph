using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public interface IAdminReportsService
{
    Task<AdminReportResponse> GetAssistantUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
    Task<AdminReportResponse> GetAssistantActivityAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
    Task<AdminReportResponse> GetMcpUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
    Task<AdminReportResponse> GetCodeReviewUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
    Task<AdminReportResponse> GetRepositoryAnalysisUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
    Task<AdminReportFiltersResponse> GetFiltersAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default);
}
