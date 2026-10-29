using System.Net;
using CodeGraph.Indexer.Client;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// GitHub webhook receiver. Authenticated by the delivery's HMAC signature, not by a user session.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/github")]
public class GitHubWebhookController(GitHubPushWebhookService webhookService) : ControllerBase
{
    /// <summary>GitHub caps webhook payloads at 25 MB.</summary>
    private const long MaxPayloadBytes = 25 * 1024 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxPayloadBytes)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);

        GitHubWebhookResult result;
        try
        {
            result = await webhookService.HandleAsync(
                Request.Headers["X-GitHub-Event"].ToString(),
                Request.Headers["X-Hub-Signature-256"].ToString(),
                buffer.ToArray(),
                ct,
                Request.Headers["X-GitHub-Delivery"].ToString());
        }
        catch (IndexerClientException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // The remote indexer's form of IndexerSubmissionConflictException: this head SHA was
            // already submitted with different arguments, so it is not queued again.
            return Ok(new { status = "ignored", message = "A sync for this head SHA was already submitted." });
        }

        return result.Outcome switch
        {
            GitHubWebhookOutcome.Unauthorized => Unauthorized(new { error = "invalid_signature", message = result.Message }),
            GitHubWebhookOutcome.Queued => Accepted(result.Accepted?.RunStatusUrl, new { status = "queued", message = result.Message, run = result.Accepted, evidence = result.Evidence?.Items }),
            GitHubWebhookOutcome.Recorded => Ok(new { status = "recorded", message = result.Message, evidence = result.Evidence?.Items }),
            _ => Ok(new { status = "ignored", message = result.Message })
        };
    }
}
