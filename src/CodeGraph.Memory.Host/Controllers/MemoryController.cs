using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Memory.Host.Controllers;

[ApiController]
[Route("api/memory")]
public class MemoryController(MemoryService memoryService, IMessageBus messageBus) : ControllerBase
{
    [HttpPost("claims/store")]
    [HttpPost("store")]
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

    [HttpGet("writes/diagnostics")]
    public async Task<ActionResult<MemoryWriteDiagnosticsResult>> GetWriteDiagnostics(
        [FromQuery] int? staleAfterMinutes,
        [FromQuery] int? sampleLimit)
    {
        var result = await memoryService.GetWriteDiagnosticsAsync(
            Math.Clamp(staleAfterMinutes ?? 15, 1, 1440),
            Math.Clamp(sampleLimit ?? 10, 1, 100));
        return Ok(result);
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<MemoryDiagnosticsResult>> GetDiagnostics(
        [FromQuery] int? staleAfterMinutes,
        [FromQuery] int? sampleLimit)
    {
        var result = await memoryService.GetDiagnosticsAsync(
            Math.Clamp(staleAfterMinutes ?? 15, 1, 1440),
            Math.Clamp(sampleLimit ?? 10, 1, 100));
        return Ok(result);
    }

    [HttpPost("cleanup/by-source")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteBySource([FromBody] MemoryCleanupBySourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(new { error = "Source is required" });

        var result = await memoryService.DeleteMemoryBySourceAsync(
            request.Source,
            request.DryRun,
            HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("cleanup/test-data")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteTestData([FromBody] MemoryCleanupTestDataRequest request)
    {
        var result = await memoryService.DeleteMemoryTestDataAsync(request.DryRun, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("cleanup/by-ids")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteByIds([FromBody] MemoryCleanupByIdsRequest request)
    {
        if (request.ClaimIds.Count == 0 && request.EntityIds.Count == 0)
            return BadRequest(new { error = "At least one claim id or entity id is required" });

        var result = await memoryService.DeleteMemoryByIdsAsync(
            request.ClaimIds,
            request.EntityIds,
            request.DryRun,
            HttpContext.RequestAborted);
        return Ok(result);
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

        return bundle == null ? NotFound() : Ok(bundle);
    }

    [HttpGet("entities/{id}/graph")]
    public async Task<ActionResult<MemoryGraphSnapshot>> GetEntityGraph(string id, [FromQuery] int? neighborLimit)
    {
        var snapshot = await memoryService.GetEntityGraphAsync(id, Math.Clamp(neighborLimit ?? 200, 1, 500));
        return snapshot.Nodes.Count == 0 ? NotFound() : Ok(snapshot);
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

        return bundle == null ? NotFound() : Ok(bundle);
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

    [HttpGet("query")]
    public async Task<ActionResult<MemoryQueryResult>> Query(
        [FromQuery] string topic,
        [FromQuery] int? hops,
        [FromQuery] int? maxNodes)
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
        if (entity == null)
            return NotFound();

        var relationships = await memoryService.GetEntityRelationshipsAsync(id);
        return Ok(new { entity, relationships });
    }
}
