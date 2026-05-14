using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMcpSensitiveColumnStore(CodeGraphDbContext db) : IMcpSensitiveColumnStore
{
    public async Task<IReadOnlyList<McpSensitiveColumnEntity>> ListAsync(CancellationToken ct = default) =>
        await db.McpSensitiveColumns.AsNoTracking()
            .OrderBy(column => column.SourceKey)
            .ThenBy(column => column.TableName)
            .ThenBy(column => column.ColumnName)
            .ToListAsync(ct);

    public async Task UpsertAsync(McpSensitiveColumnEntity entity, CancellationToken ct = default)
    {
        var sourceKey = Normalize(entity.SourceKey, "*");
        var tableName = Normalize(entity.TableName, "*");
        var columnName = entity.ColumnName.Trim().ToLowerInvariant();
        if (columnName.Length == 0)
            throw new ArgumentException("column_name is required.", nameof(entity));

        var existing = await db.McpSensitiveColumns.SingleOrDefaultAsync(
            row => row.SourceKey == sourceKey && row.TableName == tableName && row.ColumnName == columnName,
            ct);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.McpSensitiveColumns.Add(new McpSensitiveColumnEntity
            {
                SourceKey = sourceKey,
                TableName = tableName,
                ColumnName = columnName,
                Reason = Trim(entity.Reason),
                Allowed = entity.Allowed,
                IsManual = entity.IsManual,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.Reason = Trim(entity.Reason);
            existing.Allowed = entity.Allowed;
            existing.IsManual = entity.IsManual;
            existing.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var existing = await db.McpSensitiveColumns.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (existing is null)
            return false;

        db.McpSensitiveColumns.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string> GetRevisionAsync(CancellationToken ct = default)
    {
        var rows = await db.McpSensitiveColumns.AsNoTracking()
            .Select(column => new { column.UpdatedAtUtc })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return "0:0";

        var maxTicks = rows.Max(row => row.UpdatedAtUtc).Ticks;
        return $"{rows.Count}:{maxTicks}";
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
