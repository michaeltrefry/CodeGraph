using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlApplicationLogStore(CodeGraphDbContext db) : IApplicationLogStore
{
    public async Task WriteBatchAsync(
        IReadOnlyList<ApplicationLogEntryEntity> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        await db.ApplicationLogs.AddRangeAsync(entries, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApplicationLogPage> QueryAsync(
        ApplicationLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var rows = db.ApplicationLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Service))
            rows = rows.Where(row => row.Service == query.Service);
        if (!string.IsNullOrWhiteSpace(query.Level))
            rows = rows.Where(row => row.Level == query.Level);
        if (query.StartUtc.HasValue)
            rows = rows.Where(row => row.OccurredAtUtc >= query.StartUtc.Value);
        if (query.EndUtc.HasValue)
            rows = rows.Where(row => row.OccurredAtUtc <= query.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rows = rows.Where(row =>
                row.Message.Contains(search)
                || row.Category.Contains(search)
                || row.Source.Contains(search)
                || (row.Exception != null && row.Exception.Contains(search)));
        }

        var totalCount = await rows.LongCountAsync(cancellationToken);
        var entries = await rows
            .OrderByDescending(row => row.OccurredAtUtc)
            .ThenByDescending(row => row.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new ApplicationLogPage(entries, totalCount);
    }

    public Task<int> DeleteBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default) =>
        db.ApplicationLogs
            .Where(row => row.OccurredAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);
}
