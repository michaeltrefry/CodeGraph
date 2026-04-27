using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/database-sources")]
public class DatabaseSourcesController(
    IDatabaseSourceStore sourceStore,
    IIndexerOperationsService indexerOperations) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DatabaseSourceResponse>>> List()
    {
        var sources = await sourceStore.ListAsync();
        return Ok(sources.Select(ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<DatabaseSourceResponse>> Get(long id)
    {
        var source = await sourceStore.GetAsync(id);
        return source is null ? NotFound() : Ok(ToResponse(source));
    }

    [HttpPost]
    public async Task<ActionResult<DatabaseSourceResponse>> Create([FromBody] CreateDatabaseSourceRequest request)
    {
        var validation = ValidateCreateRequest(request);
        if (validation is not null)
            return BadRequest(validation);

        var now = DateTime.UtcNow;
        var entity = new DatabaseSourceEntity
        {
            ServerName = request.ServerName.Trim(),
            DatabaseName = request.DatabaseName?.Trim() ?? "",
            ConnectionString = request.ConnectionString.Trim(),
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now
        };

        var created = await sourceStore.CreateAsync(entity);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ToResponse(created));
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<DatabaseSourceResponse>> Update(long id, [FromBody] UpdateDatabaseSourceRequest request)
    {
        var validation = ValidateUpdateRequest(request);
        if (validation is not null)
            return BadRequest(validation);

        var updated = await sourceStore.UpdateAsync(
            id,
            request.ServerName?.Trim(),
            request.DatabaseName?.Trim(),
            request.ConnectionString?.Trim(),
            request.Enabled);

        return updated is null ? NotFound() : Ok(ToResponse(updated));
    }

    [HttpDelete("{id:long}")]
    public async Task<ActionResult> Delete(long id)
    {
        return await sourceStore.DeleteAsync(id) ? NoContent() : NotFound();
    }

    [HttpPost("{id:long}/sync")]
    public async Task<ActionResult<IndexerAcceptedResponse>> Sync(long id, CancellationToken ct)
    {
        var source = await sourceStore.GetAsync(id);
        if (source is null)
            return NotFound();

        var accepted = await indexerOperations.StartSyncSchemaAsync(GetUsername(), id, ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("sync-all")]
    public async Task<ActionResult<IndexerAcceptedResponse>> SyncAll(CancellationToken ct)
    {
        var accepted = await indexerOperations.StartSyncAllSchemasAsync(GetUsername(), ct);
        return Accepted(accepted.RunStatusUrl, accepted);
    }

    [HttpPost("generate-key")]
    public ActionResult GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return Ok(new { key = Convert.ToBase64String(key) });
    }

    private static string? ValidateCreateRequest(CreateDatabaseSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ServerName))
            return "ServerName is required.";

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return "ConnectionString is required.";

        return null;
    }

    private static string? ValidateUpdateRequest(UpdateDatabaseSourceRequest request)
    {
        if (request.ServerName is not null && string.IsNullOrWhiteSpace(request.ServerName))
            return "ServerName cannot be empty.";

        if (request.ConnectionString is not null && string.IsNullOrWhiteSpace(request.ConnectionString))
            return "ConnectionString cannot be empty.";

        return null;
    }

    private static DatabaseSourceResponse ToResponse(DatabaseSourceEntity e) => new(
        e.Id,
        e.ServerName,
        e.DatabaseName,
        MaskConnectionString(e.ConnectionString),
        e.Enabled,
        e.LastSyncedAt,
        e.CreatedAt,
        e.UpdatedAt);

    private static string MaskConnectionString(string connectionString)
    {
        return Regex.Replace(
            connectionString,
            @"(?i)(password|pwd)\s*=\s*[^;]+",
            "$1=***");
    }

    private string GetUsername() =>
        User.FindFirst("preferred_username")?.Value
        ?? User.FindFirst("name")?.Value
        ?? User.Identity?.Name
        ?? "unknown";
}
