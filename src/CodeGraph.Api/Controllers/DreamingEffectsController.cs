using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme + "," + McpPatAuthenticationDefaults.Scheme)]
[Route("api/work/dreaming/{project}/runs/{runId:guid}/effects")]
public sealed class DreamingEffectsController(IWorkStore work, IDreamingEffectsStore effects, IMcpHubStore hub,
    ILogger<DreamingEffectsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string project, Guid runId)
    {
        var ct = HttpContext.RequestAborted;
        var identity = User.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated && candidate.AuthenticationType == McpPatAuthenticationDefaults.Scheme);
        if (identity is not null)
        {
            long? tokenId = long.TryParse(identity.FindFirst("mcp_pat_token_id")?.Value, out var parsed) ? parsed : null;
            var denial = await McpToolAccess.EvaluateAsync(hub, "dream_runs", tokenId, requiresPersonalAccessToken: true, logger, ct);
            if (denial is not null) return Json(new { code = denial.Code, message = denial.Message }, StatusCodes.Status403Forbidden);
        }
        var target = await work.ResolveAsync(project, ct);
        if (target is null || target.Kind != WorkEntityKind.Project)
            return Json(new { code = WorkErrorCodes.NotFound, message = "The project was not found." }, StatusCodes.Status404NotFound);
        var result = await effects.GetEffectsAsync(target.Id, runId, ct);
        return result is null
            ? Json(new { code = WorkErrorCodes.NotFound, message = "The run was not found in this project." }, StatusCodes.Status404NotFound)
            : Json(result, StatusCodes.Status200OK);
    }

    private static ContentResult Json(object value, int status) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), WorkJson.Options), ContentType = "application/json", StatusCode = status
    };
}
