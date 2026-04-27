using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlIndexerRunStore(CodeGraphDbContext db) : IIndexerRunStore
{
    public async Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default)
    {
        if (run.CreatedAt == default)
        {
            run.CreatedAt = DateTime.UtcNow;
        }

        db.IndexerRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
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
}
