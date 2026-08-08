using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public class MySqlIndexerRunStore(CodeGraphDbContext db) : IIndexerRunStore
{
    public async Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default)
        => (await CreateOrGetIndexerRunAsync(run, ct)).RunId;

    public async Task<IndexerRunSubmissionResult> CreateOrGetIndexerRunAsync(
        IndexerRunEntity run,
        CancellationToken ct = default)
    {
        if (run.CreatedAt == default)
        {
            run.CreatedAt = DateTime.UtcNow;
        }

        db.IndexerRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
            return new IndexerRunSubmissionResult(run.Id, Created: true);
        }
        catch (DbUpdateException ex) when (
            !string.IsNullOrWhiteSpace(run.SubmissionKey) &&
            ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            // A concurrent request with the same durable submission identity won.
            // Return that run; the service compares the persisted request hash and
            // rejects attempts to reuse a key for different work.
            db.Entry(run).State = EntityState.Detached;
            var existingId = await db.IndexerRuns
                .AsNoTracking()
                .Where(existing =>
                    existing.RequestedByUsername == run.RequestedByUsername &&
                    existing.SubmissionKey == run.SubmissionKey)
                .Select(existing => existing.Id)
                .SingleAsync(ct);
            return new IndexerRunSubmissionResult(existingId, Created: false);
        }
    }

    public async Task UpdateIndexerRunStatusAsync(
        long runId,
        string status,
        string? message = null,
        DateTime? completedAt = null,
        string? error = null,
        CancellationToken ct = default)
    {
        var run = await db.IndexerRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.Message = message ?? run.Message;
        run.Error = error ?? run.Error;
        if (run.StartedAt is null && status is "running" or "completed" or "failed")
        {
            run.StartedAt = DateTime.UtcNow;
        }

        if (status is "completed" or "failed")
        {
            run.CompletedAt = completedAt ?? DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IndexerRunEntity?> GetIndexerRunAsync(long runId, CancellationToken ct = default)
        => await db.IndexerRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);

    public async Task<IReadOnlyList<IndexerRunEntity>> ListIndexerRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(take, 1, 200);
        var query = db.IndexerRuns.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(operation))
        {
            query = query.Where(r => r.Operation == operation);
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
        string owner,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct = default)
        => await TryClaimNextIndexerRunAsync(owner, now, leaseExpiresAt, int.MaxValue, ct);

    public async Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
        string owner,
        DateTime now,
        DateTime leaseExpiresAt,
        int maxAttempts,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        // Cancellation always wins over recovery. If the prior owner disappeared
        // after observing neither the request nor its lease expiry, terminalize the
        // run without clearing the durable cancellation signal or replaying work.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = 'canceled',
                message = 'Canceled after execution ownership expired.',
                completed_at = {now},
                next_attempt_at = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = {now}
            WHERE status = 'running'
              AND cancel_requested_at IS NOT NULL
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            """, ct);

        // Publication-producing work is deliberately at-most-once. Once its owner
        // disappears, replaying an unknown prefix could publish duplicates.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = 'failed',
                message = 'Execution ownership expired; the operation was not replayed because its side effects are not retry-safe.',
                error = 'Worker lease expired after execution started.',
                completed_at = {now},
                execution_owner = NULL,
                lease_expires_at = NULL
            WHERE status = 'running'
              AND retry_safe = FALSE
              AND cancel_requested_at IS NULL
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = 'failed',
                message = {"Failed after the configured maximum number of attempts; the queued retry was not started."},
                error = {"The retry budget was lowered or exhausted before the queued attempt started."},
                completed_at = {now},
                next_attempt_at = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = {now}
            WHERE status = 'queued'
              AND attempt_count >= {maxAttempts}
              AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
            """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = 'failed',
                message = {"Failed after the configured maximum number of attempts; the expired lease was not replayed."},
                error = {"Worker lease expired and the retry budget was exhausted."},
                completed_at = {now},
                next_attempt_at = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = {now}
            WHERE status = 'running'
              AND retry_safe = TRUE
              AND cancel_requested_at IS NULL
              AND attempt_count >= {maxAttempts}
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            """, ct);

        // Keep terminal-state maintenance out of the row-claim transaction. These
        // idempotent updates can contend across workers, and holding their range
        // locks while each worker enters SELECT ... SKIP LOCKED creates avoidable
        // deadlock cycles under concurrent polling.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var run = await db.IndexerRuns
            .FromSqlInterpolated($"""
                SELECT *
                FROM indexer_runs
                WHERE (status = 'queued'
                       AND attempt_count < {maxAttempts}
                       AND (next_attempt_at IS NULL OR next_attempt_at <= {now}))
                   OR (status = 'running'
                       AND retry_safe = TRUE
                       AND cancel_requested_at IS NULL
                       AND attempt_count < {maxAttempts}
                       AND (lease_expires_at IS NULL OR lease_expires_at <= {now}))
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .AsTracking()
            .FirstOrDefaultAsync(ct);

        if (run is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var recovered = run.Status == "running";
        run.Status = "running";
        run.ExecutionOwner = owner;
        run.LeaseExpiresAt = leaseExpiresAt;
        run.HeartbeatAt = now;
        run.NextAttemptAt = null;
        run.AttemptCount++;
        run.FencingToken++;
        run.StartedAt ??= now;
        run.CompletedAt = null;
        run.Error = null;
        if (recovered)
            run.Message = $"Recovered after lease expiry; retry attempt {run.AttemptCount} is running.";

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new IndexerRunLease(run, owner, run.FencingToken);
    }

    public async Task<IndexerRunLeaseRenewal> RenewIndexerRunLeaseAsync(
        long runId,
        string owner,
        long fencingToken,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET heartbeat_at = {now}, lease_expires_at = {leaseExpiresAt}
            WHERE id = {runId}
              AND status = 'running'
              AND execution_owner = {owner}
              AND fencing_token = {fencingToken}
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at > {now}
            """, ct);
        if (affected != 1)
            return new IndexerRunLeaseRenewal(false, false);

        var cancellationRequested = await db.IndexerRuns
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.CancelRequestedAt != null)
            .SingleAsync(ct);
        return new IndexerRunLeaseRenewal(true, cancellationRequested);
    }

    public Task<bool> CompleteIndexerRunAsync(
        IndexerRunLease lease,
        string message,
        DateTime completedAt,
        CancellationToken ct = default)
        => FinishOwnedRunAsync(lease, "completed", message, null, completedAt, ct);

    public async Task<IndexerRunFailureDisposition> FailOrRetryIndexerRunAsync(
        IndexerRunLease lease,
        string error,
        DateTime now,
        DateTime nextAttemptAt,
        int maxAttempts,
        CancellationToken ct = default)
    {
        var retry = lease.Run.RetrySafe && lease.Run.AttemptCount < Math.Max(1, maxAttempts);
        var status = retry ? "queued" : "failed";
        var message = retry
            ? $"Attempt {lease.Run.AttemptCount} failed; retry scheduled for {nextAttemptAt:O}."
            : lease.Run.RetrySafe
                ? $"Failed after {lease.Run.AttemptCount} attempt(s)."
                : "Failed without replay because the operation has non-idempotent side effects.";

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = {status},
                message = {message},
                error = {error},
                completed_at = {(retry ? null : now)},
                next_attempt_at = {(retry ? nextAttemptAt : null)},
                execution_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = {now}
            WHERE id = {lease.Run.Id}
              AND status = 'running'
              AND execution_owner = {lease.Owner}
              AND fencing_token = {lease.FencingToken}
            """, ct);

        return affected != 1
            ? IndexerRunFailureDisposition.LeaseLost
            : retry ? IndexerRunFailureDisposition.Retrying : IndexerRunFailureDisposition.Failed;
    }

    public Task<bool> CancelOwnedIndexerRunAsync(
        IndexerRunLease lease,
        string message,
        DateTime completedAt,
        CancellationToken ct = default)
        => FinishOwnedRunAsync(lease, "canceled", message, null, completedAt, ct);

    public async Task<IndexerRunEntity?> RequestIndexerRunCancellationAsync(
        long runId,
        DateTime requestedAt,
        CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET cancel_requested_at = {requestedAt},
                message = CASE
                    WHEN status = 'queued' THEN 'Canceled before execution started.'
                    WHEN status = 'running' THEN 'Cancellation requested; waiting for the owner to stop.'
                    ELSE message
                END,
                completed_at = CASE WHEN status = 'queued' THEN {requestedAt} ELSE completed_at END,
                next_attempt_at = CASE WHEN status = 'queued' THEN NULL ELSE next_attempt_at END,
                status = CASE WHEN status = 'queued' THEN 'canceled' ELSE status END
            WHERE id = {runId} AND status IN ('queued', 'running')
            """, ct);
        return await GetIndexerRunAsync(runId, ct);
    }

    private async Task<bool> FinishOwnedRunAsync(
        IndexerRunLease lease,
        string status,
        string message,
        string? error,
        DateTime completedAt,
        CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE indexer_runs
            SET status = {status},
                message = {message},
                error = {error},
                completed_at = {completedAt},
                next_attempt_at = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = {completedAt}
            WHERE id = {lease.Run.Id}
              AND status = 'running'
              AND execution_owner = {lease.Owner}
              AND fencing_token = {lease.FencingToken}
            """, ct);
        return affected == 1;
    }
}
