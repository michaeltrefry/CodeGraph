using Microsoft.AspNetCore.Mvc;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/memory")]
public class MemoryController(MemoryService memoryService, IMessageBus messageBus) : Controller
{
    [HttpPost("store")]
    public async Task<IActionResult> Store([FromBody] MemoryExtractionResult extraction, [FromQuery] string? source)
    {
        if (extraction.Nodes.Count == 0 && extraction.Edges.Count == 0)
            return BadRequest(new { error = "No nodes or edges provided" });

        await messageBus.PublishAsync(new StoreMemory
        {
            Extraction = extraction,
            Source = source ?? "api",
        });

        return Accepted(new { status = "processing", nodes = extraction.Nodes.Count, edges = extraction.Edges.Count });
    }

    [HttpGet("query")]
    public async Task<ActionResult<MemoryQueryResult>> Query([FromQuery] string topic,
        [FromQuery] int? hops, [FromQuery] int? maxNodes)
    {
        var clampedHops = Math.Clamp(hops ?? 2, 1, 5);
        var clampedMaxNodes = Math.Clamp(maxNodes ?? 5, 1, 50);

        var result = await memoryService.QueryAsync(topic, clampedHops, clampedMaxNodes);
        return Ok(result);
    }

    [HttpGet("graph")]
    public async Task<ActionResult<MemoryGraphSnapshot>> Graph([FromQuery] int? limit, [FromQuery] int? skip)
    {
        var snapshot = await memoryService.GetFullGraphAsync(limit ?? 200, skip ?? 0);
        return Ok(snapshot);
    }

    [HttpGet("entities/{id}")]
    public async Task<IActionResult> GetEntity(string id)
    {
        var entity = await memoryService.GetEntityAsync(id);
        if (entity == null) return NotFound();

        var relationships = await memoryService.GetEntityRelationshipsAsync(id);
        return Ok(new { entity, relationships });
    }
}
