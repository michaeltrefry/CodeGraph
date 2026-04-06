using Microsoft.AspNetCore.Mvc;
using CodeGraph.Models.Responses;
using CodeGraph.Services;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/graph")]
public class GraphController(IGraphOverviewService overviewService) : Controller
{
    // GET /api/graph/overview
    [HttpGet("overview")]
    public async Task<ActionResult<GraphOverviewResponse>> Overview()
    {
        return Ok(await overviewService.GetOverviewAsync());
    }
}
