using CodeGraph.Api.Auth;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
[Route("api/user/mcp-tokens")]
public class UserMcpTokensController(McpPersonalAccessTokenService tokenService) : Controller
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<McpPersonalAccessTokenMetadata>>> List(CancellationToken cancellationToken)
    {
        var tokens = await tokenService.ListForUserAsync(GetNormalizedUsername(), cancellationToken);
        return Ok(tokens);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMcpPersonalAccessTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await tokenService.CreateForUserAsync(
            GetNormalizedUsername(),
            request.Name,
            request.ExpiresInDays,
            cancellationToken);

        if (!result.Created)
        {
            return result.ErrorCode switch
            {
                "active_token_limit_reached" => Conflict(new { error = result.ErrorCode, message = result.ErrorMessage }),
                "pat_configuration_missing" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.ErrorCode, message = result.ErrorMessage }),
                _ => BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
            };
        }

        return Ok(new
        {
            token = result.Token,
            rawToken = result.RawToken
        });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Revoke(long id, CancellationToken cancellationToken)
    {
        var revoked = await tokenService.RevokeForUserAsync(GetNormalizedUsername(), id, cancellationToken);
        return revoked ? NoContent() : NotFound();
    }

    private string GetNormalizedUsername() =>
        (User.GetUsername() ?? Request.Headers["X-CodeGraph-User"].FirstOrDefault() ?? "anonymous")
        .Trim()
        .ToLowerInvariant();
}

public sealed record CreateMcpPersonalAccessTokenRequest(string Name, int ExpiresInDays);
