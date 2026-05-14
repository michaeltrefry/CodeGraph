using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/admin/mcp-hub")]
public sealed class AdminMcpHubController(
    IMcpHubStore store,
    IMcpSensitiveColumnStore sensitiveColumnStore,
    McpHubCatalogSeeder seeder) : ControllerBase
{
    [HttpGet("catalog")]
    public async Task<ActionResult<McpHubCatalogResponse>> Catalog(CancellationToken ct)
    {
        await seeder.EnsureCatalogAsync(ct);
        var providers = await store.ListProvidersAsync(ct);
        var tools = await store.ListToolsAsync(ct);
        return Ok(new McpHubCatalogResponse(
            providers.Select(MapProvider).ToList(),
            tools.Select(MapTool).ToList()));
    }

    [HttpPut("providers/{providerKey}")]
    public async Task<ActionResult> UpdateProvider(
        string providerKey,
        [FromBody] McpHubProviderUpdateRequest request,
        CancellationToken ct)
    {
        if (request.Enabled is null && request.SourceVisible is null)
            return BadRequest("No provider changes were supplied.");

        if (request.Enabled is null)
        {
            var provider = (await store.ListProvidersAsync(ct))
                .FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return NotFound();
            request = request with { Enabled = provider.Enabled };
        }

        return await store.SetProviderEnabledAsync(providerKey, request.Enabled.Value, request.SourceVisible, ct)
            ? NoContent()
            : NotFound();
    }

    [HttpPut("tools/{toolName}")]
    public async Task<ActionResult> UpdateTool(
        string toolName,
        [FromBody] McpHubToolUpdateRequest request,
        CancellationToken ct)
    {
        if (request.Enabled is null && request.DefaultSelected is null && string.IsNullOrWhiteSpace(request.AccessClass))
            return BadRequest("No tool catalog changes were supplied.");

        return await store.UpdateToolCatalogStateAsync(
            toolName,
            request.Enabled,
            request.DefaultSelected,
            request.AccessClass,
            ct)
            ? NoContent()
            : NotFound();
    }

    [HttpGet("credentials")]
    public async Task<ActionResult<IReadOnlyList<McpHubCredentialResponse>>> Credentials(CancellationToken ct)
    {
        var credentials = await store.ListCredentialsAsync(ct);
        return Ok(credentials.Select(MapCredential).ToList());
    }

    [HttpPut("credentials/{providerKey}/{credentialKey}")]
    public async Task<ActionResult> SetCredential(
        string providerKey,
        string credentialKey,
        [FromBody] McpHubCredentialWriteRequest request,
        CancellationToken ct)
    {
        await store.SetCredentialValueAsync(providerKey, credentialKey, request.Value, UpdatedBy(), ct);
        return NoContent();
    }

    [HttpDelete("credentials/{providerKey}/{credentialKey}")]
    public async Task<ActionResult> ClearCredential(
        string providerKey,
        string credentialKey,
        CancellationToken ct)
    {
        await store.SetCredentialValueAsync(providerKey, credentialKey, null, UpdatedBy(), ct);
        return NoContent();
    }

    [HttpGet("config")]
    public async Task<ActionResult<IReadOnlyList<McpHubConfigResponse>>> Config(CancellationToken ct)
    {
        var config = await store.ListConfigAsync(ct);
        return Ok(config.Select(MapConfig).ToList());
    }

    [HttpPut("config/{providerKey}/{configKey}")]
    public async Task<ActionResult> SetConfig(
        string providerKey,
        string configKey,
        [FromBody] McpHubConfigWriteRequest request,
        CancellationToken ct)
    {
        await store.SetConfigValueAsync(providerKey, configKey, request.Value, UpdatedBy(), ct);
        return NoContent();
    }

    [HttpGet("sensitive-columns")]
    public async Task<ActionResult<IReadOnlyList<McpHubSensitiveColumnResponse>>> SensitiveColumns(CancellationToken ct)
    {
        var columns = await sensitiveColumnStore.ListAsync(ct);
        return Ok(columns.Select(MapSensitiveColumn).ToList());
    }

    [HttpPut("sensitive-columns")]
    public async Task<ActionResult> UpsertSensitiveColumn(
        [FromBody] McpHubSensitiveColumnWriteRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ColumnName))
            return BadRequest("ColumnName is required.");

        await sensitiveColumnStore.UpsertAsync(new McpSensitiveColumnEntity
        {
            SourceKey = request.SourceKey ?? "*",
            TableName = request.TableName ?? "*",
            ColumnName = request.ColumnName,
            Reason = request.Reason,
            Allowed = request.Allowed,
            IsManual = true,
        }, ct);
        return NoContent();
    }

    [HttpDelete("sensitive-columns/{id:long}")]
    public async Task<ActionResult> DeleteSensitiveColumn(long id, CancellationToken ct) =>
        await sensitiveColumnStore.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<McpHubAuditResponse>>> Audit(
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var audit = await store.ListAuditAsync(limit, ct);
        return Ok(audit.Select(MapAudit).ToList());
    }

    private string? UpdatedBy() => User.GetUsername()?.Trim();

    private static McpHubProviderResponse MapProvider(McpHubProviderEntity entity) =>
        new(entity.ProviderKey, entity.DisplayName, entity.Description, entity.Enabled, entity.SourceVisible, entity.UpdatedAtUtc);

    private static McpHubToolResponse MapTool(McpHubToolEntity entity) =>
        new(
            entity.ToolName,
            entity.ProviderKey,
            entity.DisplayName,
            entity.Description,
            entity.ReadOnly,
            entity.Destructive,
            entity.Enabled,
            entity.IsAvailable,
            entity.DefaultSelected,
            entity.AccessClass,
            entity.RequiresCredential,
            entity.UpdatedAtUtc);

    private static McpHubCredentialResponse MapCredential(McpHubCredentialEntity entity) =>
        new(entity.ProviderKey, entity.CredentialKey, !string.IsNullOrWhiteSpace(entity.EncryptedValue), entity.UpdatedAtUtc, entity.UpdatedBy);

    private static McpHubConfigResponse MapConfig(McpHubConfigEntity entity) =>
        new(entity.ProviderKey, entity.ConfigKey, entity.ConfigValue, entity.UpdatedAtUtc, entity.UpdatedBy);

    private static McpHubSensitiveColumnResponse MapSensitiveColumn(McpSensitiveColumnEntity entity) =>
        new(
            entity.Id,
            entity.SourceKey,
            entity.TableName,
            entity.ColumnName,
            entity.Reason,
            entity.Allowed,
            entity.IsManual,
            entity.UpdatedAtUtc);

    private static McpHubAuditResponse MapAudit(McpHubAuditEntity entity) =>
        new(
            entity.Id,
            entity.Username,
            entity.TokenId,
            entity.ProviderKey,
            entity.ToolName,
            entity.Action,
            entity.Operation,
            entity.ResourceKey,
            entity.CredentialMode,
            entity.AuthorizationDecision,
            entity.StatusClass,
            entity.DurationMs,
            entity.Success,
            entity.Message,
            entity.CreatedAtUtc);
}
