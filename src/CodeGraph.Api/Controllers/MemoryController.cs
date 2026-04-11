using CodeGraph.Models.Memory;
using Microsoft.AspNetCore.Mvc;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/memory")]
public class MemoryController(MemoryService memoryService, IMessageBus messageBus) : Controller
{
    [HttpPost("claims/store")]
    public async Task<IActionResult> StoreClaims([FromBody] MemoryClaimExtractionResult extraction, [FromQuery] string? source)
    {
        if (extraction.Entities.Count == 0 && extraction.Claims.Count == 0 && extraction.Evidence.Count == 0)
            return BadRequest(new { error = "No entities, claims, or evidence provided" });

        var ack = await memoryService.QueueClaimsAsync(extraction, source ?? "api", "typed", messageBus, HttpContext.RequestAborted);
        return Accepted(ack);
    }

    [HttpGet("writes/{receiptId}")]
    public async Task<ActionResult<MemoryWriteReceipt>> GetWriteStatus(string receiptId)
    {
        var receipt = await memoryService.GetWriteReceiptAsync(receiptId);
        return receipt == null ? NotFound() : Ok(receipt);
    }

    [HttpGet("search")]
    public async Task<ActionResult<MemorySearchResult>> Search(
        [FromQuery] string query,
        [FromQuery] int? entityLimit,
        [FromQuery] int? claimLimit)
    {
        var result = await memoryService.SearchMemoryAsync(
            query,
            Math.Clamp(entityLimit ?? 5, 1, 25),
            Math.Clamp(claimLimit ?? 5, 1, 25));
        return Ok(result);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<MemorySubgraphResult>> Subgraph([FromBody] MemorySubgraphRequest request)
    {
        var result = await memoryService.GetMemorySubgraphAsync(request);
        return Ok(result);
    }

    [HttpGet("entities/{id}/bundle")]
    public async Task<ActionResult<MemoryEntityBundle>> GetEntityBundle(
        string id,
        [FromQuery] bool? includeSuperseded,
        [FromQuery] bool? includeConflicts,
        [FromQuery] int? neighborLimit)
    {
        var bundle = await memoryService.GetEntityBundleAsync(
            id,
            includeSuperseded ?? false,
            includeConflicts ?? true,
            Math.Clamp(neighborLimit ?? 20, 1, 500));

        if (bundle == null) return NotFound();
        return Ok(bundle);
    }

    [HttpGet("entities/{id}/graph")]
    public async Task<ActionResult<MemoryGraphSnapshot>> GetEntityGraph(string id, [FromQuery] int? neighborLimit)
    {
        var snapshot = await memoryService.GetEntityGraphAsync(id, Math.Clamp(neighborLimit ?? 200, 1, 500));
        if (snapshot.Nodes.Count == 0) return NotFound();
        return Ok(snapshot);
    }

    [HttpGet("claims/{id}")]
    [HttpGet("claims/{id}/bundle")]
    public async Task<ActionResult<MemoryClaimBundle>> GetClaimBundle(
        string id,
        [FromQuery] bool? includeSupersessionChain,
        [FromQuery] bool? includeConflicts,
        [FromQuery] bool? includeEvidence)
    {
        var bundle = await memoryService.GetClaimBundleAsync(
            id,
            includeSupersessionChain ?? true,
            includeConflicts ?? true,
            includeEvidence ?? true);

        if (bundle == null) return NotFound();
        return Ok(bundle);
    }

    [HttpPost("frontier/expand")]
    public async Task<ActionResult<MemoryFrontierExpansionResult>> ExpandFrontier(
        [FromBody] MemoryFrontierExpansionRequest request)
    {
        if ((request.FrontierEntityIds?.Count ?? 0) == 0 && (request.FrontierClaimIds?.Count ?? 0) == 0)
            return BadRequest(new { error = "At least one frontier entity or claim id is required" });

        var result = await memoryService.ExpandMemoryFrontierAsync(request);
        return Ok(result);
    }

    [HttpPost("render-summary")]
    public async Task<ActionResult<MemorySummaryRenderResult>> RenderSummary(
        [FromBody] MemorySummaryRenderRequest request)
    {
        if ((request.EntityIds?.Count ?? 0) == 0 && (request.ClaimIds?.Count ?? 0) == 0)
            return BadRequest(new { error = "At least one entity or claim id is required" });

        var result = await memoryService.RenderMemorySummaryAsync(request);
        return Ok(result);
    }

    [HttpPost("migrate-legacy")]
    public async Task<ActionResult<MemoryLegacyMigrationResult>> MigrateLegacy()
    {
        var result = await memoryService.MigrateLegacyRelationshipsAsync();
        return Ok(result);
    }

    [HttpPost("migrate-observations")]
    public async Task<ActionResult<MemoryObservationMigrationResult>> MigrateObservations()
    {
        var result = await memoryService.MigrateObservationsAsync();
        return Ok(result);
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
        var snapshot = await memoryService.GetFullGraphAsync(
            Math.Clamp(limit ?? 200, 1, 500),
            Math.Max(skip ?? 0, 0));
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
