using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/admin/reports")]
public class AdminReportsController(IAdminReportsService reportsService) : ControllerBase
{
    [HttpGet("assistant/usage")]
    public Task<ActionResult<AdminReportResponse>> GetAssistantUsage(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReportAsync(() => reportsService.GetAssistantUsageAsync(request, cancellationToken));

    [HttpGet("assistant/activity")]
    public Task<ActionResult<AdminReportResponse>> GetAssistantActivity(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReportAsync(() => reportsService.GetAssistantActivityAsync(request, cancellationToken));

    [HttpGet("mcp/usage")]
    public Task<ActionResult<AdminReportResponse>> GetMcpUsage(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReportAsync(() => reportsService.GetMcpUsageAsync(request, cancellationToken));

    [HttpGet("code-review/usage")]
    public Task<ActionResult<AdminReportResponse>> GetCodeReviewUsage(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReportAsync(() => reportsService.GetCodeReviewUsageAsync(request, cancellationToken));

    [HttpGet("repository-analysis/usage")]
    public Task<ActionResult<AdminReportResponse>> GetRepositoryAnalysisUsage(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReportAsync(() => reportsService.GetRepositoryAnalysisUsageAsync(request, cancellationToken));

    [HttpGet("filters")]
    public async Task<ActionResult<AdminReportFiltersResponse>> GetFilters(
        [FromQuery] AdminReportQueryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reportsService.GetFiltersAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private static async Task<ActionResult<AdminReportResponse>> ExecuteReportAsync(
        Func<Task<AdminReportResponse>> action)
    {
        try
        {
            return new OkObjectResult(await action());
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(ex.Message);
        }
    }
}
