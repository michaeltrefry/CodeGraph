using Microsoft.AspNetCore.Mvc;
using TC.CodeGraphApi.Models.Requests;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Services.Analyzers;

namespace TC.CodeGraphApi.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController(
    IProjectQueryService queryService,
    IProjectService projectService,
    IImpactAnalysisService impactService) : Controller
{
    // GET /api/projects?search=&page=1&pageSize=25
    [HttpGet]
    public async Task<ActionResult<ProjectListResponse>> List(
        [FromQuery] string? search,
        [FromQuery] string? group,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Ok(await queryService.ListAsync(search, group, page, pageSize));
    }

    // GET /api/projects/{name}
    [HttpGet("{name}")]
    public async Task<ActionResult<ProjectDetailResponse>> Detail(string name)
    {
        var result = await queryService.GetDetailAsync(name);
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/projects/{name}/health
    [HttpGet("{name}/health")]
    public async Task<ActionResult<ProjectHealthResponse>> Health(string name)
    {
        var result = await queryService.GetHealthAsync(name);
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/projects/{name}/security
    [HttpGet("{name}/security")]
    public async Task<ActionResult<ProjectSecurityResponse>> Security(string name)
    {
        var result = await queryService.GetSecurityAsync(name);
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/projects/{name}/metrics?dotnetProject=X&top=20
    [HttpGet("{name}/metrics")]
    public async Task<ActionResult<IReadOnlyList<FileMetrics>>> Metrics(
        string name,
        [FromQuery] string? dotnetProject,
        [FromQuery] int top = 50)
    {
        return Ok(await queryService.GetMetricsAsync(name, dotnetProject, top));
    }

    // GET /api/projects/{name}/hotspots?top=10
    [HttpGet("{name}/hotspots")]
    public async Task<ActionResult<IReadOnlyList<FileMetrics>>> Hotspots(
        string name,
        [FromQuery] int top = 10)
    {
        return Ok(await queryService.GetHotspotsAsync(name, top));
    }

    // GET /api/projects/{name}/nodes?label=Method&dotnetProject=TC.OrdersApi.Services&page=1&pageSize=50
    [HttpGet("{name}/nodes")]
    public async Task<ActionResult<NodeListResponse>> Nodes(
        string name,
        [FromQuery] string? label,
        [FromQuery] string? dotnetProject,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        return Ok(await queryService.GetNodesAsync(name, label, dotnetProject, page, pageSize));
    }

    // GET /api/projects/{name}/batch-status
    [HttpGet("{name}/batch-status")]
    public async Task<ActionResult<AnalysisBatchResponse>> BatchStatus(string name)
    {
        var result = await queryService.GetBatchStatusAsync(name);
        return result is null ? NotFound() : Ok(result);
    }

    // DELETE /api/projects/{name}
    [HttpDelete("{name}")]
    public async Task<ActionResult> Delete(string name)
    {
        var deleted = await projectService.DeleteRepositoryAsync(name);
        return deleted ? NoContent() : NotFound();
    }

    // GET /api/projects/{name}/readme
    [HttpGet("{name}/readme")]
    public async Task<ActionResult> Readme(string name)
    {
        var content = await queryService.GetReadmeAsync(name);
        return content is null ? NotFound() : Ok(new { content });
    }

    // GET /api/projects/{name}/impact?node=QualifiedName&depth=3
    [HttpGet("{name}/impact")]
    public async Task<ActionResult<ImpactReport>> Impact(
        string name,
        [FromQuery] string node,
        [FromQuery] int depth = 3)
    {
        if (string.IsNullOrWhiteSpace(node))
            return BadRequest("'node' query parameter is required (qualified name or node name).");

        var result = await impactService.AnalyzeImpactAsync(node, name, Math.Clamp(depth, 1, 5));
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/projects/{name}/impact/file?path=relative/file.cs&depth=3
    [HttpGet("{name}/impact/file")]
    public async Task<ActionResult<ImpactReport>> FileImpact(
        string name,
        [FromQuery] string path,
        [FromQuery] int depth = 3)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("'path' query parameter is required.");

        var result = await impactService.AnalyzeFileImpactAsync(name, path, Math.Clamp(depth, 1, 5));
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost(nameof(ReAnalyze))]
    public async Task<ActionResult<AnalysisBatchResponse>> ReAnalyze([FromBody] ReAnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Repo))
            return BadRequest("Repo entry is required.");

        var result = await projectService.ReAnalyzeRepository(request.Repo);
        return result is null ? NotFound() : Ok(result);
    }
}
