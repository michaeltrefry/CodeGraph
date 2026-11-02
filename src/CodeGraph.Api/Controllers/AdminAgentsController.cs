using CodeGraph.Api.Auth;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// Agents and their tokens (CG-F16). Administrators only: the policy authenticates with the
/// sign-in scheme, so no personal access token (a person's or an agent's) reaches these endpoints.
/// </summary>
[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/admin/agents")]
public sealed class AdminAgentsController(AgentService agents) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentSummary>>> List(CancellationToken ct) =>
        Ok(await agents.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentRequest request, CancellationToken ct) =>
        Answer(await agents.CreateAsync(request.Name, User.GetUsername()?.Trim().ToLowerInvariant() ?? "anonymous", ct));

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] UpdateAgentRequest request, CancellationToken ct) =>
        Answer(await agents.UpdateAsync(id, request.Name, request.Disabled, ct));

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var result = await agents.DeleteAsync(id, ct);
        return result.ErrorCode is null ? NoContent() : Refusal(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("{id:long}/tokens")]
    public async Task<IActionResult> Tokens(long id, CancellationToken ct) =>
        Answer(await agents.ListTokensAsync(id, ct));

    [HttpPost("{id:long}/tokens")]
    public async Task<IActionResult> CreateToken(long id, [FromBody] CreateMcpPersonalAccessTokenRequest request, CancellationToken ct)
    {
        var result = await agents.CreateTokenAsync(id, request.Name, request.ExpiresInDays, request.ToolNames, ct);
        if (result.Value is not { } created)
            return Refusal(result.ErrorCode!, result.ErrorMessage);
        if (!created.Created)
            return TokenRefusal(created);
        return Ok(new { token = created.Token, rawToken = created.RawToken });
    }

    [HttpPut("{id:long}/tokens/{tokenId:long}/tools")]
    public async Task<IActionResult> UpdateTokenTools(long id, long tokenId, [FromBody] UpdateMcpPersonalAccessTokenToolsRequest request, CancellationToken ct)
    {
        var result = await agents.UpdateTokenToolsAsync(id, tokenId, request.ToolNames ?? [], ct);
        if (result.Value is not { } updated)
            return Refusal(result.ErrorCode!, result.ErrorMessage);
        return updated.Created ? Ok(updated.Token) : TokenRefusal(updated);
    }

    [HttpDelete("{id:long}/tokens/{tokenId:long}")]
    public async Task<IActionResult> RevokeToken(long id, long tokenId, CancellationToken ct)
    {
        var result = await agents.RevokeTokenAsync(id, tokenId, ct);
        if (result.ErrorCode is not null)
            return Refusal(result.ErrorCode, result.ErrorMessage);
        return result.Value ? NoContent() : NotFound(new { error = "token_not_found", message = "MCP token not found." });
    }

    private IActionResult Answer<T>(AgentResult<T> result) =>
        result.ErrorCode is null ? Ok(result.Value) : Refusal(result.ErrorCode, result.ErrorMessage);

    private IActionResult Refusal(string code, string? message) => code switch
    {
        AgentErrors.NotFound => NotFound(new { error = code, message }),
        AgentErrors.Conflict => Conflict(new { error = code, message }),
        _ => BadRequest(new { error = code, message })
    };

    // The same answers as a person's token endpoints (UserMcpTokensController).
    private IActionResult TokenRefusal(McpPersonalAccessTokenCreateResult result) => result.ErrorCode switch
    {
        "token_not_found" => NotFound(new { error = result.ErrorCode, message = result.ErrorMessage }),
        "active_token_limit_reached" => Conflict(new { error = result.ErrorCode, message = result.ErrorMessage }),
        "pat_configuration_missing" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.ErrorCode, message = result.ErrorMessage }),
        _ => BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
    };
}

public sealed record CreateAgentRequest(string? Name);

public sealed record UpdateAgentRequest(string? Name, bool? Disabled);
