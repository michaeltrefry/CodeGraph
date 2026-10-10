using Microsoft.AspNetCore.Mvc;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Overlays;

namespace CodeGraph.Api.Controllers;

public record DoNotTrustRequest(bool DoNotTrust);

[ApiController]
[Route("api/nodes")]
public class NodesController(INodeQueryService nodeQueryService, ICommitScopeResolver commitScopes) : Controller
{
    // GET /api/nodes/by-file?project=X&filePath=Y&line=N&sha=<full commit sha>
    [HttpGet("by-file")]
    public async Task<ActionResult<NodeByFileResponse>> ByFile(
        [FromQuery] string project,
        [FromQuery] string filePath,
        [FromQuery] int? line = null,
        [FromQuery] string? sha = null)
    {
        var scope = await commitScopes.ResolveAsync(project, sha);
        if (scope.NotIndexed is { } notIndexed)
            return this.NotIndexed(notIndexed);

        var result = await nodeQueryService.FindNodeByFileAsync(project, filePath, line, scope);
        return result is null
            ? NotFound(new { message = "No node found for file", project, filePath, line, commitSha = scope.Sha })
            : Ok(result);
    }

    // GET /api/nodes/{id}?sha=<full commit sha>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<NodeDetailResponse>> Detail(long id, [FromQuery] string? sha = null)
    {
        var scope = await commitScopes.ResolveAsync(null, sha);
        if (scope.NotIndexed is { } notIndexed)
            return this.NotIndexed(notIndexed);

        var result = await nodeQueryService.GetDetailAsync(id, scope);
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/nodes/{id}/source?sha=<full commit sha>
    [HttpGet("{id:long}/source")]
    public async Task<ActionResult<NodeSourceResponse>> Source(long id, [FromQuery] string? sha = null)
    {
        var scope = await commitScopes.ResolveAsync(null, sha);
        if (scope.NotIndexed is { } notIndexed)
            return this.NotIndexed(notIndexed);

        var result = await nodeQueryService.GetNodeSourceAsync(id, scope);
        return result is null ? NotFound() : Ok(result);
    }

    // PUT /api/nodes/{id}/do-not-trust
    [HttpPut("{id:long}/do-not-trust")]
    public async Task<ActionResult> SetDoNotTrust(long id, [FromBody] DoNotTrustRequest request)
    {
        await nodeQueryService.SetDoNotTrustAsync(id, request.DoNotTrust);
        return Ok();
    }

    // GET /api/nodes/search?q=&project=&label=&page=1&pageSize=25&sha=<full commit sha>
    [HttpGet("search")]
    public async Task<ActionResult<NodeListResponse>> Search(
        [FromQuery] string q = "%",
        [FromQuery] string? project = null,
        [FromQuery] string? label = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? sha = null)
    {
        var scope = await commitScopes.ResolveAsync(project, sha);
        if (scope.NotIndexed is { } notIndexed)
            return this.NotIndexed(notIndexed);

        return Ok(await nodeQueryService.SearchAsync(q, project, label, page, pageSize, scope));
    }
}
