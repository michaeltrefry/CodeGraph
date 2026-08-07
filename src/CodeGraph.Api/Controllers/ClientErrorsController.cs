using CodeGraph.Api.Auth;
using CodeGraph.Models.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/client-errors")]
[Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    private const int MaxMessageLength = 4_096;
    private const int MaxStackLength = 32_768;
    private const int MaxUrlLength = 2_048;
    private const int MaxUserAgentLength = 512;

    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public ActionResult Report([FromBody] ClientErrorReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("Message is required.");
        if (request.Message.Length > MaxMessageLength)
            return BadRequest($"Message cannot exceed {MaxMessageLength} characters.");
        if (request.Stack?.Length > MaxStackLength)
            return BadRequest($"Stack cannot exceed {MaxStackLength} characters.");
        if (request.Url?.Length > MaxUrlLength)
            return BadRequest($"Url cannot exceed {MaxUrlLength} characters.");
        if (request.UserAgent?.Length > MaxUserAgentLength)
            return BadRequest($"UserAgent cannot exceed {MaxUserAgentLength} characters.");

        logger.LogError(
            "Unhandled browser error at {PageUrl} from {UserAgent}: {ErrorMessage}\n{ClientStack}",
            Normalize(request.Url),
            Normalize(request.UserAgent),
            request.Message.Trim(),
            Normalize(request.Stack));

        return Accepted();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
