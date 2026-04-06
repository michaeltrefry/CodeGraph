using Microsoft.AspNetCore.Mvc;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Controllers;

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
