using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlIndexerRunStore
{
    // Execution targets, request JSON, and idempotency hashes are deliberately left untouched.
    // Only the two operations whose display targets do not identify their work need a snapshot.
    internal static string? RepositorySetQueueIdentity(string? argsJson)
    {
        try
        {
            using var json = JsonDocument.Parse(argsJson ?? "null");
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("repos", out var repos) || repos.ValueKind != JsonValueKind.Array)
                return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repo in repos.EnumerateArray())
            {
                if (repo.ValueKind != JsonValueKind.String) return null;
                var name = repo.GetString()!.Split("::", 2)[0].Trim().ToLowerInvariant();
                if (name.Length == 0) return null;
                names.Add(name);
            }
            return names.Count == 0 ? null : HashQueueIdentity(names.Order(StringComparer.Ordinal));
        }
        catch (JsonException) { return null; }
    }

    internal static string? OverlayQueueIdentity(string? repository, string? requestedRef)
        => string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(requestedRef)
            ? null
            : HashQueueIdentity(new[] { repository.Trim().ToLowerInvariant(), NormalizeOverlayRef(requestedRef) });

    private static string NormalizeOverlayRef(string requestedRef)
    {
        var reference = requestedRef.Trim();
        return reference.Length is 40 or 64 && reference.All(Uri.IsHexDigit)
            ? reference.ToLowerInvariant() : reference;
    }

    private static string HashQueueIdentity(IEnumerable<string> values)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    private async Task<string?> GetQueueWorkIdentityAsync(IndexerRunEntity run, CancellationToken ct)
    {
        if (run.Operation == "process_repositories") return RepositorySetQueueIdentity(run.ArgsJson);
        if (run.Operation != "branch_overlay") return null;
        // The overlay exists before submission, but may not yet have indexer_run_id attached.
        long overlayId = 0;
        try
        {
            using var json = JsonDocument.Parse(run.ArgsJson ?? "null");
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("overlayId", out var value) && value.ValueKind == JsonValueKind.Number)
                value.TryGetInt64(out overlayId);
        }
        catch (JsonException) { }
        if (overlayId <= 0 && !long.TryParse(run.Target, out overlayId)) return null;
        var overlay = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<OverlayScope>(
            new CommandDefinition("SELECT repository AS Repository, requested_ref AS RequestedRef FROM branch_overlays WHERE id = @overlayId",
                new { overlayId }, transaction: db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return OverlayQueueIdentity(overlay?.Repository, overlay?.RequestedRef);
    }

    private sealed class OverlayScope
    {
        public string? Repository { get; set; }
        public string? RequestedRef { get; set; }
    }

    private async Task MaterializeQueueResolutionAsync(CancellationToken ct)
    {
        // Backfill pre-migration work, including still-active runs. Unknown legacy metadata receives
        // an empty inspected marker and cannot supersede anything. NULL is uninspected. Paging
        // bounds memory, and unknown legacy rows are inspected only once, not on every poll.
        long afterId = 0;
        while (true)
        {
            var runs = await db.IndexerRuns.AsNoTracking()
                .Where(r => r.Id > afterId && r.QueueWorkIdentity == null &&
                    (r.Operation == "process_repositories" || r.Operation == "branch_overlay"))
                .OrderBy(r => r.Id).Take(500).ToListAsync(ct);
            foreach (var run in runs)
            {
                var identity = await GetQueueWorkIdentityAsync(run, ct) ?? "";
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE indexer_runs SET queue_work_identity = {identity} WHERE id = {run.Id} AND queue_work_identity IS NULL", ct);
            }
            if (runs.Count < 500) break;
            afterId = runs[^1].Id;
        }
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE indexer_runs r
            JOIN indexer_runs later ON later.operation = r.operation AND later.id > r.id
              AND later.status = 'completed'
              AND ((r.operation NOT IN ('process_repositories','branch_overlay') AND later.target <=> r.target)
                OR (r.operation IN ('process_repositories','branch_overlay') AND r.queue_work_identity IS NOT NULL AND r.queue_work_identity <> ''
                    AND later.queue_work_identity = r.queue_work_identity))
            SET r.failure_resolved_at = UTC_TIMESTAMP(6)
            WHERE r.status = 'failed' AND r.dismissed_at IS NULL AND r.failure_resolved_at IS NULL
            """, ct);
    }

    private const string QueueCte = """
        WITH queue AS (
            SELECT 'indexer-run' AS Kind, r.id AS Id, r.operation AS Operation, r.target AS Target,
                r.status AS Status,
                CASE WHEN r.operation = 'process_repository' AND JSON_VALID(r.args_json)
                    THEN COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(r.args_json, '$.requestedByUsername')), 'null'),
                        NULLIF(JSON_UNQUOTE(JSON_EXTRACT(r.args_json, '$.requestedBy')), 'null'), r.requested_by_username)
                    ELSE r.requested_by_username END AS RequestedByUsername,
                r.message AS Message, COALESCE(NULLIF(r.error,''),r.message) AS Error,
                r.created_at AS CreatedAt, r.started_at AS StartedAt, r.next_attempt_at AS NextAttemptAt,
                r.attempt_count AS AttemptCount,
                (r.status IN ('queued','running') AND r.cancel_requested_at IS NULL) AS Cancelable
            FROM indexer_runs r
            WHERE r.status IN ('queued','running') OR (r.status = 'failed' AND r.dismissed_at IS NULL AND r.failure_resolved_at IS NULL)
            UNION ALL
            SELECT 'analysis-batch', b.id, 'analysis-batch', b.repo,
                CASE WHEN b.status = 'failed' THEN 'failed' ELSE 'running' END,
                b.requested_by_username, NULL, CASE WHEN b.status = 'failed' THEN COALESCE(NULLIF(b.error,''), 'Analysis batch failed.') ELSE NULL END,
                b.submitted_at, b.submitted_at, NULL, 0, FALSE
            FROM analysis_batches b
            WHERE b.status NOT IN ('completed','failed') OR (b.status = 'failed' AND b.dismissed_at IS NULL
                AND NOT EXISTS (SELECT 1 FROM analysis_batches later WHERE later.repo = b.repo AND later.id > b.id AND later.status = 'completed'))
            UNION ALL
            SELECT 'scheduled-job', s.id, s.job_type, s.name, 'running', NULL, NULL, NULL,
                COALESCE(s.lease_acquired_utc,s.last_run_started_utc), COALESCE(s.lease_acquired_utc,s.last_run_started_utc), NULL, 0, FALSE
            FROM job_schedules s
            WHERE s.lease_expires_utc > UTC_TIMESTAMP(3)
                AND s.job_type NOT IN ('Discover','ReIndexAll','ProcessBatchAnalysis','LinkAndDetect','DetectCommunities')
            UNION ALL
            SELECT 'scheduled-job', s.id, s.job_type, s.name, 'failed', NULL, NULL,
                COALESCE(NULLIF(s.failure_error,''),'Scheduled job failed.'), s.failure_at_utc, NULL, NULL, 0, FALSE
            FROM job_schedules s WHERE s.failure_at_utc IS NOT NULL AND s.failure_dismissed_at IS NULL
        )
        """;

    public async Task<JobQueueResponse> GetJobQueueAsync(int limit, CancellationToken ct = default)
    {
        await MaterializeQueueResolutionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var items = await connection.QueryAsync<QueueRow>(new CommandDefinition(QueueCte + """
            SELECT * FROM queue
            ORDER BY FIELD(Status,'running','failed','queued'),
                CASE WHEN Status='failed' THEN CreatedAt END DESC, CreatedAt, Id
            LIMIT @limit
            """, new { limit = Math.Clamp(limit, 1, 500) }, cancellationToken: ct));
        var counts = (await connection.QueryAsync<QueueCount>(new CommandDefinition(
            QueueCte + "SELECT Status, COUNT(*) AS Count FROM queue GROUP BY Status", cancellationToken: ct)))
            .ToDictionary(row => row.Status, row => (int)Math.Min(int.MaxValue, row.Count));
        return new(items.Select(r => r.ToEntry()).ToList(), counts.GetValueOrDefault("running"),
            counts.GetValueOrDefault("queued"), counts.GetValueOrDefault("failed"), DateTime.UtcNow);
    }

    public async Task<bool> DismissJobAsync(string kind, long id, CancellationToken ct = default)
    {
        // Conditional updates are atomic with failures/completions and do not touch leases.
        if (kind == "indexer-run")
        {
            await MaterializeQueueResolutionAsync(ct);
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE indexer_runs SET dismissed_at = UTC_TIMESTAMP(6) WHERE id = {id} AND status = 'failed' AND dismissed_at IS NULL AND failure_resolved_at IS NULL", ct) == 1;
        }
        if (kind == "analysis-batch")
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE analysis_batches b SET dismissed_at = UTC_TIMESTAMP(6)
                WHERE id = {id} AND status = 'failed' AND dismissed_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM (SELECT id, repo, status FROM analysis_batches) later
                    WHERE later.repo = b.repo AND later.id > b.id AND later.status = 'completed')
                """, ct) == 1;
        if (kind == "scheduled-job")
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_schedules SET failure_dismissed_at = UTC_TIMESTAMP(3) WHERE id = {id} AND failure_at_utc IS NOT NULL AND failure_dismissed_at IS NULL", ct) == 1;
        return false;
    }

    public async Task<int> DeleteFinishedIndexerRunsBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var limit = Math.Min(batchSize, 5000);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MaterializeQueueResolutionAsync(ct);
        // Keep a completed witness while an older matching run is active: it can still fail
        // after this transaction. Unknown special metadata conservatively retains the witness.
        // Deletion also intentionally frees old submission keys for reuse after retention.
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM indexer_runs WHERE id IN (
                SELECT id FROM (
                    SELECT r.id FROM indexer_runs r
                    WHERE COALESCE(r.completed_at,r.created_at) < {cutoffUtc}
                      AND (r.status = 'canceled'
                        OR (r.status = 'failed' AND (r.dismissed_at IS NOT NULL OR r.failure_resolved_at IS NOT NULL))
                        OR (r.status = 'completed' AND NOT EXISTS (
                            SELECT 1 FROM indexer_runs active
                            WHERE active.id < r.id AND active.operation = r.operation AND active.status IN ('queued','running')
                            AND ((r.operation NOT IN ('process_repositories','branch_overlay') AND active.target <=> r.target)
                              OR (r.operation IN ('process_repositories','branch_overlay') AND
                                (active.queue_work_identity IS NULL OR active.queue_work_identity = ''
                                 OR r.queue_work_identity IS NULL OR r.queue_work_identity = ''
                                 OR active.queue_work_identity = r.queue_work_identity))))))
                    ORDER BY COALESCE(r.completed_at,r.created_at), r.id LIMIT {limit}
                ) eligible
            )
            """, ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    private sealed class QueueCount
    {
        public string Status { get; set; } = "";
        public long Count { get; set; }
    }

    private sealed class QueueRow
    {
        public string Kind { get; set; } = "";
        public long Id { get; set; }
        public string Operation { get; set; } = "";
        public string? Target { get; set; }
        public string Status { get; set; } = "";
        public string? RequestedByUsername { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public bool Cancelable { get; set; }
        public JobQueueEntry ToEntry() => new(Kind, Id, Operation, Target, Status, RequestedByUsername,
            Message, Error, CreatedAt, StartedAt, NextAttemptAt, AttemptCount, Cancelable);
    }
}
