using CodeGraph.Api.Auth;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/indexer")]
public class IndexerController(IIndexerOperationsService indexerOperations) : ControllerBase
{
    [HttpPost("repositories/process")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ProcessRepositories(
        [FromBody] ProcessRequest request,
        CancellationToken ct)
    {
        if (request.Repos is not { Count: > 0 })
            return BadRequest("At least one repo entry is required.");

        if (request.Repos.Count > 500)
            return BadRequest("Maximum 500 repos per request.");

        return await AcceptSubmissionAsync(() => indexerOperations.StartProcessRepositoriesAsync(
            GetUsername(), request, GetSubmissionKey(), ct));
    }

    [HttpPost("repositories/reindex-all")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ReIndexAll(CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartReIndexAllAsync(
            GetUsername(), GetSubmissionKey(), ct));
    }

    [HttpPost("repositories/discover")]
    public async Task<ActionResult<IndexerAcceptedResponse>> Discover(
        [FromBody] DiscoverRequest? request,
        CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartDiscoverAsync(
            GetUsername(), request, GetSubmissionKey(), ct));
    }

    [HttpPost("link")]
    public async Task<ActionResult<IndexerAcceptedResponse>> Link(CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartLinkAsync(
            GetUsername(), GetSubmissionKey(), ct));
    }

    [HttpPost("communities/detect")]
    public async Task<ActionResult<IndexerAcceptedResponse>> DetectCommunities(CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartDetectCommunitiesAsync(
            GetUsername(), GetSubmissionKey(), ct));
    }

    [HttpPost("link-and-detect")]
    public async Task<ActionResult<IndexerAcceptedResponse>> LinkAndDetect(CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartLinkAndDetectAsync(
            GetUsername(), GetSubmissionKey(), ct));
    }

    [HttpPost("batch-analysis/process")]
    public async Task<ActionResult<IndexerAcceptedResponse>> ProcessBatchAnalysis([FromQuery] string? repo, CancellationToken ct)
    {
        return await AcceptSubmissionAsync(() => indexerOperations.StartProcessBatchAnalysisAsync(
            GetUsername(), repo, GetSubmissionKey(), ct));
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

    [HttpPost("runs/{runId:long}/cancel")]
    public async Task<ActionResult<IndexerRunResponse>> CancelRun(long runId, CancellationToken ct)
    {
        var run = await indexerOperations.CancelRunAsync(runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    private string GetUsername() =>
        User.FindFirst("preferred_username")?.Value
        ?? User.FindFirst("name")?.Value
        ?? User.Identity?.Name
        ?? "unknown";

    private string? GetSubmissionKey()
        => Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.ToString() : null;

    private async Task<ActionResult<IndexerAcceptedResponse>> AcceptSubmissionAsync(
        Func<Task<IndexerAcceptedResponse>> submit)
    {
        try
        {
            var accepted = await submit();
            return Accepted(accepted.RunStatusUrl, accepted);
        }
        catch (IndexerSubmissionConflictException ex)
        {
            return Conflict(new { error = "idempotency_conflict", message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "invalid_request", message = ex.Message });
        }
    }
}
