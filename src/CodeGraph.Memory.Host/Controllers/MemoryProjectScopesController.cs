using CodeGraph.Data;
using CodeGraph.Models.Memory;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Memory.Host.Controllers;

/// <summary>
/// Scopes existing memory, of every user, to a project by the repositories it names (CG-167): the API
/// calls it when a project is created or gains a repository. Not a per-user operation, so only the
/// system identity may call it.
/// </summary>
[ApiController]
[Route("api/memory/project-scopes")]
public class MemoryProjectScopesController(IMemoryTenantContext tenantContext) : ControllerBase
{
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill([FromBody] MemoryProjectScopeBackfillRequest request)
    {
        if (tenantContext.Username != MemoryTenantContext.SystemUsername)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", message = "Only the system identity may backfill project scopes." });
        if (string.IsNullOrWhiteSpace(request.ProjectKey))
            return BadRequest(new { error = "ProjectKey is required" });
        if (HttpContext.RequestServices.GetService<IMemoryProjectScopeBackfill>() is not { } backfill)
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "not_supported", message = "This memory store does not keep project scopes." });

        await backfill.BackfillAsync(request.ProjectKey.Trim(), request.Repositories, HttpContext.RequestAborted);
        return NoContent();
    }
}
