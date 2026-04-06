using Microsoft.AspNetCore.Mvc;
using CodeGraph.Models.Responses;
using CodeGraph.Services;

namespace CodeGraph.Api.Controllers;

public record DoNotTrustRequest(bool DoNotTrust);

[ApiController]
[Route("api/nodes")]
public class NodesController(INodeQueryService nodeQueryService) : Controller
{
    // GET /api/nodes/by-file?project=X&filePath=Y&line=N
    [HttpGet("by-file")]
    public async Task<ActionResult<object>> ByFile(
        [FromQuery] string project,
        [FromQuery] string filePath,
        [FromQuery] int? line = null)
    {
        var nodeId = await nodeQueryService.FindNodeByFileAsync(project, filePath, line);
        return nodeId is null
            ? NotFound(new { message = "No node found for file", project, filePath, line })
            : Ok(new { nodeId = nodeId.Value });
    }

    // GET /api/nodes/{id}
    [HttpGet("{id:long}")]
    public async Task<ActionResult<NodeDetailResponse>> Detail(long id)
    {
        var result = await nodeQueryService.GetDetailAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    // GET /api/nodes/{id}/source
    [HttpGet("{id:long}/source")]
    public async Task<ActionResult<NodeSourceResponse>> Source(long id)
    {
        var result = await nodeQueryService.GetNodeSourceAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    // PUT /api/nodes/{id}/do-not-trust
    [HttpPut("{id:long}/do-not-trust")]
    public async Task<ActionResult> SetDoNotTrust(long id, [FromBody] DoNotTrustRequest request)
    {
        await nodeQueryService.SetDoNotTrustAsync(id, request.DoNotTrust);
        return Ok();
    }

    // GET /api/nodes/search?q=&project=&label=&page=1&pageSize=25
    [HttpGet("search")]
    public async Task<ActionResult<NodeListResponse>> Search(
        [FromQuery] string q = "%",
        [FromQuery] string? project = null,
        [FromQuery] string? label = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Ok(await nodeQueryService.SearchAsync(q, project, label, page, pageSize));
    }
}
