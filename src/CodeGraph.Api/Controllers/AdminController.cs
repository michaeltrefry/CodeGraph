using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Prompts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/admin")]
public class AdminController(
    IAdminStore adminStore,
    IAgentPromptService agentPromptService) : ControllerBase
{
    [HttpGet("admins")]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> ListAdmins()
    {
        var admins = await adminStore.ListAdminUsersAsync();
        return Ok(admins.Select(MapAdmin).ToList());
    }

    [HttpPost("admins")]
    public async Task<ActionResult<AdminUserResponse>> AddAdmin([FromBody] AddAdminRequest request)
    {
        var username = NormalizeUsername(request.Username);
        if (string.IsNullOrWhiteSpace(username))
            return BadRequest("Username is required.");

        if (await adminStore.IsAdminAsync(username))
            return Conflict($"'{username}' is already an admin.");

        var created = await adminStore.AddAdminUserAsync(new AdminUserEntity
        {
            Username = username,
            CreatedAt = DateTime.UtcNow
        });

        return Ok(MapAdmin(created));
    }

    [HttpDelete("admins/{username}")]
    public async Task<ActionResult> RemoveAdmin(string username)
    {
        var normalized = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalized))
            return BadRequest("Username is required.");

        return await adminStore.RemoveAdminUserAsync(normalized)
            ? NoContent()
            : NotFound();
    }

    [HttpGet("prompts")]
    public async Task<ActionResult<IReadOnlyList<AgentPromptGroupResponse>>> ListPrompts()
    {
        var prompts = await agentPromptService.ListAsync();
        return Ok(prompts
            .GroupBy(p => new { p.Category, p.CategoryDisplayName })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.CategoryDisplayName,
                SortOrder = g.Min(p => p.SortOrder),
                Prompts = g.OrderBy(p => p.SortOrder).Select(MapPrompt).ToList()
            })
            .OrderBy(g => g.SortOrder)
            .Select(g => new AgentPromptGroupResponse(g.Category, g.CategoryDisplayName, g.Prompts))
            .ToList());
    }

    [HttpPut("prompts/{key}")]
    public async Task<ActionResult<AgentPromptResponse>> UpdatePrompt(string key, [FromBody] UpdateAgentPromptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PromptText))
            return BadRequest("PromptText is required.");

        var username = User.GetUsername();
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized();

        var updated = await agentPromptService.SaveOverrideAsync(key, request.PromptText, username);
        return updated is null ? NotFound() : Ok(MapPrompt(updated));
    }

    [HttpDelete("prompts/{key}")]
    public async Task<ActionResult> ResetPrompt(string key)
    {
        var reset = await agentPromptService.ResetOverrideAsync(key);
        return reset is null ? NotFound() : NoContent();
    }

    private static string NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "" : username.Trim().ToLowerInvariant();

    private static AdminUserResponse MapAdmin(AdminUserEntity entity) =>
        new(entity.Username, entity.CreatedAt);

    private static AgentPromptResponse MapPrompt(AgentPromptAdminModel prompt) => new(
        prompt.Key,
        prompt.Category,
        prompt.CategoryDisplayName,
        prompt.DisplayName,
        prompt.Description,
        prompt.DefaultText,
        prompt.EffectiveText,
        prompt.HasOverride,
        prompt.UpdatedBy,
        prompt.UpdatedAt);
}
