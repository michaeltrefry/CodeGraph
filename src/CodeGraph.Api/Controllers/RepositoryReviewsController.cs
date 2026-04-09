using System.Text;
using System.Text.Json;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Reviews;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/projects/{repo}/code-review")]
public class RepositoryReviewsController(
    IRepositoryReviewService reviewService,
    ILogger<RepositoryReviewsController> logger) : Controller
{
    private static readonly JsonSerializerOptions CamelOpts = CodeGraph.Models.CodeGraphJsonDefaults.CamelCase;

    [HttpPost]
    public async Task<ActionResult<StartRepositoryReviewResponse>> Start(
        string repo,
        [FromBody] StartRepositoryReviewRequest request,
        CancellationToken ct)
    {
        var reviewRunId = await reviewService.StartReviewAsync(repo, request.Mode, ct);
        return Accepted(new StartRepositoryReviewResponse(reviewRunId, "queued"));
    }

    [HttpGet("latest")]
    public async Task<ActionResult<RepositoryReviewResponse>> Latest(string repo, CancellationToken ct)
    {
        var review = await reviewService.GetLatestReviewAsync(repo, ct);
        return review is null ? NotFound() : Ok(review);
    }

    [HttpGet("{reviewRunId:long}")]
    public async Task<ActionResult<RepositoryReviewResponse>> Get(string repo, long reviewRunId, CancellationToken ct)
    {
        var review = await reviewService.GetReviewAsync(reviewRunId, ct);
        if (!BelongsToRepo(review, repo))
            return NotFound();

        return Ok(review);
    }

    [HttpGet("{reviewRunId:long}/stream")]
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

        logger.LogInformation("Repository review stream opened for {Repo} review run {ReviewRunId}", repo, reviewRunId);

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
                    message = string.IsNullOrWhiteSpace(review.Run.Error) ? "Repository review failed." : review.Run.Error
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

    private static bool BelongsToRepo(RepositoryReviewResponse? review, string repo)
        => review is not null &&
           string.Equals(review.Run.Repo, repo, StringComparison.OrdinalIgnoreCase);

    private static string BuildProgressMessage(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "queued" => "Queued for server-side repository review execution.",
            "running" => "Repository review is gathering project evidence and synthesizing results.",
            _ => "Repository review status updated."
        };
}
