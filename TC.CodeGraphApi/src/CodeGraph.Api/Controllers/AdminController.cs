using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController(
    IAdminService adminService,
    IAdminUserService adminUserService,
    ISettingsService settingsService,
    IExclusionService exclusionService,
    IWikiService wikiService,
    IMcpDocService mcpDocService) : Controller
{
    // ── Existing operations ──

    /// <summary>
    /// Publish ProcessRepository messages for one or more repos.
    /// </summary>
    [HttpPost("processRepos")]
    public async Task<ActionResult<ProcessReposResponse>> ProcessRepositories([FromBody] ProcessRequest request)
    {
        if (request.Repos is not { Count: > 0 })
            return BadRequest("At least one repo entry is required.");

        if (request.Repos.Count > 500)
            return BadRequest("Maximum 500 repos per request.");

        try
        {
            return Ok(await adminService.ProcessRepositoriesAsync(request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Re-index all known repos.
    /// </summary>
    [HttpPost("reIndexAll")]
    public async Task<ActionResult<ProcessReposResponse>> ReIndexAllRepos()
    {
        return Ok(await adminService.ReIndexAllAsync());
    }

    /// <summary>
    /// Run cross-repo linking across all indexed repositories.
    /// </summary>
    [HttpPost("link")]
    public async Task<ActionResult> Link()
    {
        await adminService.LinkAsync(HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Re-run community detection (Louvain clustering) on existing cross-repo edges.
    /// </summary>
    [HttpPost("detectCommunities")]
    public async Task<ActionResult> DetectCommunities()
    {
        await adminService.DetectCommunitiesAsync(HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Run cross-repo linking then community detection in one operation.
    /// </summary>
    [HttpPost("linkAndDetect")]
    public async Task<ActionResult> LinkAndDetect()
    {
        await adminService.LinkAndDetectAsync(HttpContext.RequestAborted);
        return Ok();
    }

    [HttpPost("processBatchAnalysis")]
    public async Task<ActionResult> ProcessBatchAnalysis(string? repo = null)
    {
        await adminService.ProcessBatchAnalysisAsync(repo);
        return Ok();
    }

    /// <summary>
    /// Discover all GitLab projects, then publish a ProcessRepository message for each.
    /// </summary>
    [HttpPost("discover")]
    public async Task<ActionResult<DiscoverResponse>> Discover([FromBody] DiscoverRequest? request = null)
    {
        try
        {
            return Ok(await adminService.DiscoverAsync(request));
        }
        catch (RegexParseException ex)
        {
            return BadRequest($"Invalid regex pattern: {ex.Message}");
        }
    }

    // ── Settings management ──

    /// <summary>
    /// Get current effective settings as JSON (ConnectionString excluded).
    /// </summary>
    [HttpGet("settings")]
    public async Task<ActionResult<string>> GetSettings()
    {
        var json = await settingsService.GetEffectiveSettingsJsonAsync();
        return Content(json, "application/json");
    }

    /// <summary>
    /// Update settings overrides (JSON merge over Consul base).
    /// </summary>
    [HttpPut("settings")]
    public async Task<ActionResult> UpdateSettings([FromBody] System.Text.Json.JsonElement settingsJson)
    {
        var username = User.GetUsername();
        if (string.IsNullOrEmpty(username))
            return Unauthorized();

        await settingsService.UpdateOverridesAsync(settingsJson.GetRawText(), username);
        return Ok();
    }

    // ── Admin user management ──

    /// <summary>
    /// List all admin usernames.
    /// </summary>
    [HttpGet("admins")]
    public async Task<ActionResult<IReadOnlyList<string>>> ListAdmins()
    {
        return Ok(await adminUserService.ListAsync());
    }

    /// <summary>
    /// Add an admin username.
    /// </summary>
    [HttpPost("admins")]
    public async Task<ActionResult> AddAdmin([FromBody] AddAdminRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest("Username is required.");

        var added = await adminUserService.AddAsync(request.Username.Trim());
        return added ? Ok() : Conflict($"'{request.Username}' is already an admin.");
    }

    /// <summary>
    /// Remove an admin username.
    /// </summary>
    [HttpDelete("admins/{username}")]
    public async Task<ActionResult> RemoveAdmin(string username)
    {
        var removed = await adminUserService.RemoveAsync(username);
        return removed ? NoContent() : NotFound();
    }

    // ── Section management ──

    [HttpGet("sections")]
    public async Task<ActionResult<IReadOnlyList<WikiSectionResponse>>> ListSections()
    {
        return Ok(await wikiService.ListSectionsAsync());
    }

    [HttpPost("sections")]
    public async Task<ActionResult<WikiSectionResponse>> CreateSection([FromBody] WikiSectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest("Title is required.");

        var result = await wikiService.CreateSectionAsync(request);
        return result is null ? Conflict("Section with this slug already exists.") : Ok(result);
    }

    [HttpPut("sections/{id:long}")]
    public async Task<ActionResult<WikiSectionResponse>> UpdateSection(long id, [FromBody] WikiSectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest("Title is required.");

        var result = await wikiService.UpdateSectionAsync(id, request);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("sections/{id:long}")]
    public async Task<ActionResult> DeleteSection(long id)
    {
        return await wikiService.DeleteSectionAsync(id) ? NoContent() : NotFound();
    }

    // ── Exclusion rules ──

    [HttpGet("exclusions")]
    public async Task<ActionResult<IReadOnlyList<ExclusionRuleResponse>>> ListExclusions()
    {
        var rules = await exclusionService.ListRulesAsync();
        return Ok(rules.Select(r => new ExclusionRuleResponse
        {
            Id = r.Id,
            TargetType = r.TargetType,
            TargetValue = r.TargetValue,
            ExclusionType = r.ExclusionType,
            Reason = r.Reason,
            CreatedBy = r.CreatedBy,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList());
    }

    [HttpPost("exclusions")]
    public async Task<ActionResult<ExclusionRuleResponse>> CreateExclusion([FromBody] ExclusionRuleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetValue))
            return BadRequest("TargetValue is required.");

        if (request.TargetType is not ("group" or "repository"))
            return BadRequest("TargetType must be 'group' or 'repository'.");

        if (request.ExclusionType is not ("complete" or "no_analysis"))
            return BadRequest("ExclusionType must be 'complete' or 'no_analysis'.");

        var username = User.GetUsername() ?? "unknown";
        try
        {
            var created = await exclusionService.CreateRuleAsync(
                request.TargetType, request.TargetValue.Trim(), request.ExclusionType, request.Reason, username);
            return Ok(new ExclusionRuleResponse
            {
                Id = created.Id,
                TargetType = created.TargetType,
                TargetValue = created.TargetValue,
                ExclusionType = created.ExclusionType,
                Reason = created.Reason,
                CreatedBy = created.CreatedBy,
                CreatedAt = created.CreatedAt,
                UpdatedAt = created.UpdatedAt
            });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Conflict("An exclusion rule for this target already exists.");
        }
    }

    [HttpPut("exclusions/{id:long}")]
    public async Task<ActionResult<ExclusionRuleResponse>> UpdateExclusion(long id, [FromBody] UpdateExclusionRuleRequest request)
    {
        if (request.ExclusionType is not ("complete" or "no_analysis"))
            return BadRequest("ExclusionType must be 'complete' or 'no_analysis'.");

        var updated = await exclusionService.UpdateRuleAsync(id, request.ExclusionType, request.Reason);
        if (updated is null) return NotFound();

        return Ok(new ExclusionRuleResponse
        {
            Id = updated.Id,
            TargetType = updated.TargetType,
            TargetValue = updated.TargetValue,
            ExclusionType = updated.ExclusionType,
            Reason = updated.Reason,
            CreatedBy = updated.CreatedBy,
            CreatedAt = updated.CreatedAt,
            UpdatedAt = updated.UpdatedAt
        });
    }

    [HttpDelete("exclusions/{id:long}")]
    public async Task<ActionResult> DeleteExclusion(long id)
    {
        return await exclusionService.DeleteRuleAsync(id) ? NoContent() : NotFound();
    }

    // ── MCP Documentation ──

    /// <summary>
    /// Regenerate MCP documentation pages from current tool metadata.
    /// </summary>
    [HttpPost("mcp/regenerate")]
    public async Task<ActionResult> RegenerateMcpDocs()
    {
        await mcpDocService.RegenerateAsync();
        return Ok(new { message = "MCP documentation regenerated." });
    }
}

public record AddAdminRequest(string Username);
