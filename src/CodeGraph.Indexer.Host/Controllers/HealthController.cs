using CodeGraph.Extractors.TypeScript;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Indexer.Host.Controllers;

[ApiController]
[Route("health")]
public class HealthController(TypeScriptServerManager typeScriptServerManager) : ControllerBase
{
    [HttpGet("sidecar")]
    public async Task<ActionResult<object>> Sidecar(CancellationToken ct)
    {
        var available = await typeScriptServerManager.EnsureStartedAsync(ct);
        return available
            ? Ok(new { status = "healthy", sidecar = "typescript" })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unhealthy", sidecar = "typescript" });
    }
}
