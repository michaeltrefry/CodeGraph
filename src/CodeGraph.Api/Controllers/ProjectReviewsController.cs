using System.Text;
using System.Text.Json;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Reviews;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/projects/{repo}")]
public class ProjectReviewsController(
    IProjectReviewService reviewService,
    ILogger<ProjectReviewsController> logger) : Controller
{
    private static readonly JsonSerializerOptions CamelOpts = CodeGraph.Models.CodeGraphJsonDefaults.CamelCase;

    [HttpPost("reviews")]
    public async Task<ActionResult<StartProjectReviewResponse>> Start(
        string repo,
        [FromBody] StartProjectReviewRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
            return BadRequest("ProjectName is required.");

        var reviewRunId = await reviewService.StartReviewAsync(repo, request.ProjectName, request.Mode, ct);
        return Accepted(new StartProjectReviewResponse(reviewRunId, "queued"));
    }

    [HttpGet("reviews/latest")]
    public async Task<ActionResult<ProjectReviewResponse>> Latest(
        string repo,
        [FromQuery] string projectName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return BadRequest("projectName is required.");

        var review = await reviewService.GetLatestReviewAsync(repo, projectName, ct);
        return review is null ? NotFound() : Ok(review);
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<ProjectDiagnosticsResponse>> Diagnostics(
        string repo,
        [FromQuery] string? dotnetProject,
        CancellationToken ct)
    {
        var response = await reviewService.GetDiagnosticsAsync(
            repo,
            string.IsNullOrWhiteSpace(dotnetProject) ? null : dotnetProject,
            ct);
        return Ok(response);
    }

    [HttpGet("reviews/{reviewRunId:long}/stream")]
    public async Task Stream(string repo, long reviewRunId, CancellationToken ct)
    {
        var review = await reviewService.GetReviewAsync(reviewRunId, ct);
        if (!BelongsToRepo(review, repo))
        {
            Response.StatusCode = 404;
            Response.ContentType = "application/json";
            await Response.WriteAsync("{\"error\":\"Review run not found\"}", ct);
            return;
        }

        logger.LogInformation("Project review stream opened for {Repo} review run {ReviewRunId}", repo, reviewRunId);

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.Body.FlushAsync(ct);

        var lastStatus = string.Empty;
        var lastHeartbeat = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            review = await reviewService.GetReviewAsync(reviewRunId, ct);
            if (!BelongsToRepo(review, repo))
            {
                await WriteSseEventAsync("error", new { reviewRunId, message = "Review run no longer exists." }, ct);
                return;
            }

            if (!string.Equals(lastStatus, review!.Run.Status, StringComparison.OrdinalIgnoreCase))
            {
                lastStatus = review.Run.Status;
                await WriteSseEventAsync("status", new
                {
                    reviewRunId,
                    status = review.Run.Status,
                    startedAt = review.Run.StartedAt,
                    completedAt = review.Run.CompletedAt,
                    error = review.Run.Error
                }, ct);
            }
            else if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(3))
            {
                lastHeartbeat = DateTime.UtcNow;
                await WriteSseEventAsync("progress", new
                {
                    reviewRunId,
                    status = review.Run.Status,
                    message = BuildProgressMessage(review.Run.Status)
                }, ct);
            }

            if (string.Equals(review.Run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var finding in review.Findings)
                    await WriteSseEventAsync("finding", finding, ct);

                await WriteSseEventAsync("completed", review, ct);
                return;
            }

            if (string.Equals(review.Run.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSseEventAsync("error", new
                {
                    reviewRunId,
                    status = review.Run.Status,
                    message = string.IsNullOrWhiteSpace(review.Run.Error) ? "Project review failed." : review.Run.Error
                }, ct);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task WriteSseEventAsync(string type, object content, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { type, content }, CamelOpts);
        var line = $"data: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await Response.Body.WriteAsync(bytes, ct);
        await Response.Body.FlushAsync(ct);
    }

    private static bool BelongsToRepo(ProjectReviewResponse? review, string repo)
        => review is not null &&
           string.Equals(review.Run.Project, repo, StringComparison.OrdinalIgnoreCase);

    private static string BuildProgressMessage(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "queued" => "Queued for server-side review execution.",
            "running" => "Review is gathering evidence and synthesizing findings.",
            _ => "Review status updated."
        };
}
