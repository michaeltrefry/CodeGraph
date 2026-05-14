using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
[Route("api/user/mcp-tokens")]
public class UserMcpTokensController(
    McpPersonalAccessTokenService tokenService,
    IMcpHubStore hubStore) : Controller
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<McpPersonalAccessTokenMetadata>>> List(CancellationToken cancellationToken)
    {
        var tokens = await tokenService.ListForUserAsync(GetNormalizedUsername(), cancellationToken);
        return Ok(tokens);
    }

    [HttpGet("tools")]
    public async Task<ActionResult<IReadOnlyList<McpHubToolResponse>>> Tools(CancellationToken cancellationToken)
    {
        var tools = await hubStore.ListToolsAsync(cancellationToken);
        return Ok(tools
            // Effective entitlement requires both admin-owned `enabled` and system-owned
            // `is_available` — see Shortcut sc-1055.
            .Where(tool => tool.Enabled && tool.IsAvailable)
            .OrderBy(tool => tool.ProviderKey)
            .ThenBy(tool => tool.ToolName)
            .Select(tool => new McpHubToolResponse(
                tool.ToolName,
                tool.ProviderKey,
                tool.DisplayName,
                tool.Description,
                tool.ReadOnly,
                tool.Destructive,
                tool.Enabled,
                tool.IsAvailable,
                tool.DefaultSelected,
                tool.AccessClass,
                tool.RequiresCredential,
                tool.UpdatedAtUtc))
            .ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMcpPersonalAccessTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await tokenService.CreateForUserAsync(
            GetNormalizedUsername(),
            request.Name,
            request.ExpiresInDays,
            request.ToolNames,
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

    [HttpPut("{id:long}/tools")]
    public async Task<IActionResult> UpdateTools(
        long id,
        [FromBody] UpdateMcpPersonalAccessTokenToolsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await tokenService.UpdateToolsForUserAsync(
            GetNormalizedUsername(),
            id,
            request.ToolNames ?? [],
            cancellationToken);

        if (!result.Created)
        {
            return result.ErrorCode switch
            {
                "token_not_found" => NotFound(new { error = result.ErrorCode, message = result.ErrorMessage }),
                _ => BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
            };
        }

        return Ok(result.Token);
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

public sealed record CreateMcpPersonalAccessTokenRequest(
    string Name,
    int ExpiresInDays,
    IReadOnlyList<string>? ToolNames = null);

public sealed record UpdateMcpPersonalAccessTokenToolsRequest(
    IReadOnlyList<string>? ToolNames);
