using CodeGraph.Data;
using CodeGraph.Models.Memory;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Memory.Host.Controllers;

/// <summary>
/// Scopes existing memory, of every user, to a project by the repositories it names (CG-167): the API
/// calls it when a project is created or gains a repository, and the Jobs host's daily reconciliation
/// reads the memory watermark and scopes the memory written since its last run (CG-172). Not a
/// per-user operation, so only the system identity may call it.
/// </summary>
[ApiController]
[Route("api/memory/project-scopes")]
public class MemoryProjectScopesController(IMemoryTenantContext tenantContext) : ControllerBase
{
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill([FromBody] MemoryProjectScopeBackfillRequest request)
    {
        if (Refuse() is { } refused)
            return refused;
        if (string.IsNullOrWhiteSpace(request.ProjectKey))
            return BadRequest(new { error = "ProjectKey is required" });
        if (Backfill() is not { } backfill)
            return NotSupported();

        await backfill.BackfillAsync(request.ProjectKey.Trim(), request.Repositories, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("watermark")]
    public async Task<IActionResult> Watermark([FromBody] MemoryProjectScopeWatermarkRequest request)
    {
        if (Refuse() is { } refused)
            return refused;
        if (Backfill() is not { } backfill)
            return NotSupported();

        return Ok(await backfill.GetWatermarkAsync(request.Since, HttpContext.RequestAborted));
    }

    [HttpPost("backfill-range")]
    public async Task<IActionResult> BackfillRange([FromBody] MemoryProjectScopeRangeBackfillRequest request)
    {
        if (Refuse() is { } refused)
            return refused;
        if (request.Projects.Any(project => string.IsNullOrWhiteSpace(project.ProjectKey)))
            return BadRequest(new { error = "Every project needs a ProjectKey" });
        if (Backfill() is not { } backfill)
            return NotSupported();

        var projects = request.Projects
            .Select(project => new ProjectRepositories(project.ProjectKey.Trim(), project.Repositories))
            .ToList();
        await backfill.BackfillRangeAsync(projects, request.After, request.UpTo, HttpContext.RequestAborted);
        return NoContent();
    }

    private IActionResult? Refuse() =>
        tenantContext.Username == MemoryTenantContext.SystemUsername
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", message = "Only the system identity may backfill project scopes." });

    private IMemoryProjectScopeBackfill? Backfill() => HttpContext.RequestServices.GetService<IMemoryProjectScopeBackfill>();

    private ObjectResult NotSupported() =>
        StatusCode(StatusCodes.Status501NotImplemented, new { error = "not_supported", message = "This memory store does not keep project scopes." });
}
