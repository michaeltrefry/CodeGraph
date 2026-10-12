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
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);

        var result = await webhookService.HandleAsync(
            Request.Headers["X-GitHub-Event"].ToString(),
            Request.Headers["X-Hub-Signature-256"].ToString(),
            buffer.ToArray(),
            ct);

        return result.Outcome switch
        {
            GitHubWebhookOutcome.Unauthorized => Unauthorized(new { error = "invalid_signature", message = result.Message }),
            GitHubWebhookOutcome.Queued => Accepted(result.Accepted?.RunStatusUrl, new { status = "queued", message = result.Message, run = result.Accepted }),
            _ => Ok(new { status = "ignored", message = result.Message })
        };
    }
}
