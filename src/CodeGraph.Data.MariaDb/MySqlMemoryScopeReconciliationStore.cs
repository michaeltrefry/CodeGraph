using System.Text.Json;
using CodeGraph.Models.Memory;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// The memory scope reconciliation's state (CG-172) in the work database (migration 116). The pending
/// repositories of a project are written by the project's own write (<see cref="AddPendingAsync"/>, in
/// its transaction) and read, completed or failed here, each under the pending row's lock.
/// </summary>
public sealed class MySqlMemoryScopeReconciliationStore(IOptions<MariaDbStorageOptions> options, TimeProvider? timeProvider = null)
    : IMemoryScopeReconciliationStore
{
    private const int MaxErrorLength = 2000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PendingMemoryScopeBackfill>> ListPendingAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<(string ProjectKey, string RepositoriesJson, DateTime EnqueuedAt, int Attempts, string? LastError)>(new CommandDefinition(
            "SELECT project_key, repositories_json, enqueued_at, attempts, last_error FROM memory_scope_backfill_pending ORDER BY enqueued_at, project_key",
            cancellationToken: ct));
        return rows.Select(row => new PendingMemoryScopeBackfill(row.ProjectKey, Repositories(row.RepositoriesJson), row.EnqueuedAt, row.Attempts, row.LastError)).ToList();
    }

    public async Task CompletePendingAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT repositories_json FROM memory_scope_backfill_pending WHERE project_key = @projectKey FOR UPDATE",
            new { projectKey }, transaction, cancellationToken: ct));
        if (current is null)
            return;

        var done = repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = Repositories(current).Where(name => !done.Contains(name)).ToList();
        await connection.ExecuteAsync(new CommandDefinition(
            left.Count == 0
                ? "DELETE FROM memory_scope_backfill_pending WHERE project_key = @projectKey"
                : "UPDATE memory_scope_backfill_pending SET repositories_json = @json WHERE project_key = @projectKey",
            new { projectKey, json = JsonSerializer.Serialize(left) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task FailPendingAsync(string projectKey, string error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE memory_scope_backfill_pending SET attempts = attempts + 1, last_attempt_at = @now, last_error = @error WHERE project_key = @projectKey",
            new { projectKey, now = clock.GetUtcNow().UtcDateTime, error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error },
            cancellationToken: ct));
    }

    public async Task<MemoryScopeWatermark?> GetWatermarkAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(long EntityId, long ClaimId, long EvidenceId)?>(new CommandDefinition(
            "SELECT entity_id, claim_id, evidence_id FROM memory_scope_watermark WHERE id = 1", cancellationToken: ct));
        return row is { } mark ? new MemoryScopeWatermark(mark.EntityId, mark.ClaimId, mark.EvidenceId) : null;
    }

    public async Task SetWatermarkAsync(MemoryScopeWatermark watermark, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO memory_scope_watermark (id, entity_id, claim_id, evidence_id, updated_at) VALUES (1, @EntityId, @ClaimId, @EvidenceId, @now) " +
            "ON DUPLICATE KEY UPDATE entity_id = VALUES(entity_id), claim_id = VALUES(claim_id), evidence_id = VALUES(evidence_id), updated_at = VALUES(updated_at)",
            new { watermark.EntityId, watermark.ClaimId, watermark.EvidenceId, now = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>
    /// Adds the repositories to the project's pending set, in the caller's transaction: a project write
    /// records the repositories it added with the membership itself, so no crash between the two loses
    /// them. Taken after the project's row (the work store's lock order), under the pending row's lock.
    /// </summary>
    internal static async Task AddPendingAsync(MySqlConnection connection, MySqlTransaction transaction, string projectKey,
        IReadOnlyCollection<string> repositories, DateTime now, CancellationToken ct)
    {
        if (repositories.Count == 0)
            return;
        var current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT repositories_json FROM memory_scope_backfill_pending WHERE project_key = @projectKey FOR UPDATE",
            new { projectKey }, transaction, cancellationToken: ct));
        var union = (current is null ? [] : Repositories(current)).ToList();
        union.AddRange(repositories.Where(name => !union.Contains(name, StringComparer.OrdinalIgnoreCase)));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO memory_scope_backfill_pending (project_key, repositories_json, enqueued_at, attempts) VALUES (@projectKey, @json, @now, 0) " +
            "ON DUPLICATE KEY UPDATE repositories_json = VALUES(repositories_json)",
            new { projectKey, json = JsonSerializer.Serialize(union), now }, transaction, cancellationToken: ct));
    }

    private static List<string> Repositories(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
