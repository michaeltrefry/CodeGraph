using System.Security.Claims;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Indexer.Host.Controllers;

[ApiController]
[Route("api/indexer")]
public class IndexerController(IIndexerOperationsService indexerOperations) : ControllerBase
{
    [HttpPost("repositories/process")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ProcessRepositories(
        [FromBody] ProcessRequest request,
        CancellationToken ct)
    {
        if (request.Repos is not { Count: > 0 })
            return BadRequest(new { error = "invalid_request", message = "At least one repo entry is required." });

        if (request.Repos.Count > 500)
            return BadRequest(new { error = "invalid_request", message = "Maximum 500 repos per request." });

        var accepted = await indexerOperations.StartProcessRepositoriesAsync(GetUsername(), request, ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("repositories/reindex-all")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ReIndexAll(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartReIndexAllAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("repositories/discover")]
    public async Task<ActionResult<IndexerAcceptedResponse>> Discover(
        [FromBody] DiscoverRequest? request,
        CancellationToken ct)
    {
        var accepted = await indexerOperations.StartDiscoverAsync(GetUsername(), request, ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("schemas/{sourceId:long}/sync")]
    public async Task<ActionResult<IndexerAcceptedResponse>> SyncSchema(long sourceId, CancellationToken ct)
    {
        if (sourceId <= 0)
            return BadRequest(new { error = "invalid_request", message = "Database source id must be positive." });

        var accepted = await indexerOperations.StartSyncSchemaAsync(GetUsername(), sourceId, ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("schemas/sync-all")]
    public async Task<ActionResult<IndexerAcceptedResponse>> SyncAllSchemas(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartSyncAllSchemasAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("link")]
    public async Task<ActionResult<IndexerAcceptedResponse>> Link(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartLinkAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("communities/detect")]
    public async Task<ActionResult<IndexerAcceptedResponse>> DetectCommunities(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartDetectCommunitiesAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("link-and-detect")]
    public async Task<ActionResult<IndexerAcceptedResponse>> LinkAndDetect(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartLinkAndDetectAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("batch-analysis/process")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ProcessBatchAnalysis([FromQuery] string? repo, CancellationToken ct)
    {
        var accepted = await indexerOperations.StartProcessBatchAnalysisAsync(GetUsername(), repo, ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<IndexerRunResponse>>> ListRuns(
        [FromQuery] string? status,
        [FromQuery] string? operation,
        [FromQuery] int take,
        CancellationToken ct)
    {
        var runs = await indexerOperations.ListRunsAsync(status, operation, take <= 0 ? 50 : take, ct);
        return Ok(runs);
    }

    [HttpGet("runs/{runId:long}")]
    public async Task<ActionResult<IndexerRunResponse>> GetRun(long runId, CancellationToken ct)
    {
        var run = await indexerOperations.GetRunAsync(runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    private string GetUsername() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue("username")
        ?? User.Identity?.Name
        ?? "unknown";
}
