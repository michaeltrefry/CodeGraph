using System.Text.Json;
using CodeGraph.Models.Memory;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// The memory scope reconciliation's state (CG-172) in the work database (migration 116). The pending
/// repositories of a project are written by the project's own write (<see cref="AddPendingAsync(MySqlConnection, MySqlTransaction, string, IReadOnlyCollection{string}, DateTime, CancellationToken)"/>,
/// in its transaction) and read, completed or failed here, each under the pending row's lock.
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

    public async Task AddPendingAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await AddPendingAsync(connection, transaction, projectKey, repositories, clock.GetUtcNow().UtcDateTime, ct);
        await transaction.CommitAsync(ct);
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

        // A repository the project no longer has (or a project since deleted) has nothing left to scope. Read
        // after taking the pending row's lock, without locking: a project write that adds a repository
        // commits its membership with its pending row, and waits for this lock to write that row, so a
        // repository pending here is always a member by the time it can be read.
        var members = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT r.repository_name FROM work_project_repositories r JOIN work_projects p ON p.id = r.project_id WHERE p.project_key = @projectKey",
            new { projectKey }, transaction, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = Repositories(current);
        var left = pending.Where(name => members.Contains(name) && !done.Contains(name)).ToList();
        if (left.Count == pending.Count)
            return;

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
        var row = await connection.QuerySingleOrDefaultAsync<(long EntitySeq, long ClaimSeq, long EvidenceId)?>(new CommandDefinition(
            "SELECT entity_seq, claim_seq, evidence_id FROM memory_scope_watermark WHERE id = 1", cancellationToken: ct));
        return row is { } mark ? new MemoryScopeWatermark(mark.EntitySeq, mark.ClaimSeq, mark.EvidenceId) : null;
    }

    public async Task SetWatermarkAsync(MemoryScopeWatermark watermark, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO memory_scope_watermark (id, entity_seq, claim_seq, evidence_id, updated_at) VALUES (1, @EntitySeq, @ClaimSeq, @EvidenceId, @now) " +
            "ON DUPLICATE KEY UPDATE entity_seq = VALUES(entity_seq), claim_seq = VALUES(claim_seq), evidence_id = VALUES(evidence_id), updated_at = VALUES(updated_at)",
            new { watermark.EntitySeq, watermark.ClaimSeq, watermark.EvidenceId, now = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
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
