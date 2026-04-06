using Microsoft.AspNetCore.Mvc;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services.Analyzers;

namespace TC.CodeGraphApi.Controllers;

[ApiController]
[Route("api/clusters")]
public class ClustersController(ICommunityDetectionService communityDetection) : Controller
{
    [HttpGet]
    public async Task<ActionResult<ClusterOverviewResponse>> GetClusters()
    {
        return Ok(await communityDetection.GetClusterOverviewAsync());
    }

    [HttpGet("graph")]
    public async Task<ActionResult<ClusterGraphResponse>> GetClusterGraph()
    {
        return Ok(await communityDetection.GetClusterGraphAsync());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ClusterDetailResponse>> GetClusterDetail(int id)
    {
        var detail = await communityDetection.GetClusterDetailAsync(id);
        if (detail is null) return NotFound();
        return Ok(detail);
    }
}
