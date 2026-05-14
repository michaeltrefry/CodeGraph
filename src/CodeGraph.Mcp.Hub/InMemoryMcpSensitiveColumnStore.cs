using CodeGraph.Data;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Fallback sensitive-column store used when no MariaDB provider is registered. Mirrors
/// <see cref="InMemoryMcpHubStore"/> — process-local, non-durable.
/// </summary>
internal sealed class InMemoryMcpSensitiveColumnStore : IMcpSensitiveColumnStore
{
    private readonly object gate = new();
    private readonly List<McpSensitiveColumnEntity> rows = [];
    private long nextId = 1;

    public Task<IReadOnlyList<McpSensitiveColumnEntity>> ListAsync(CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<McpSensitiveColumnEntity>>(rows.Select(Clone).ToList());
    }

    public Task UpsertAsync(McpSensitiveColumnEntity entity, CancellationToken ct = default)
    {
        var sourceKey = Normalize(entity.SourceKey, "*");
        var tableName = Normalize(entity.TableName, "*");
        var columnName = entity.ColumnName.Trim().ToLowerInvariant();
        if (columnName.Length == 0)
            throw new ArgumentException("column_name is required.", nameof(entity));

        lock (gate)
        {
            var now = DateTime.UtcNow;
            var existing = rows.SingleOrDefault(row =>
                row.SourceKey == sourceKey && row.TableName == tableName && row.ColumnName == columnName);
            if (existing is null)
            {
                rows.Add(new McpSensitiveColumnEntity
                {
                    Id = nextId++,
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
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult(rows.RemoveAll(row => row.Id == id) > 0);
    }

    public Task<string> GetRevisionAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            if (rows.Count == 0)
                return Task.FromResult("0:0");
            return Task.FromResult($"{rows.Count}:{rows.Max(row => row.UpdatedAtUtc).Ticks}");
        }
    }

    private static McpSensitiveColumnEntity Clone(McpSensitiveColumnEntity row) => new()
    {
        Id = row.Id,
        SourceKey = row.SourceKey,
        TableName = row.TableName,
        ColumnName = row.ColumnName,
        Reason = row.Reason,
        Allowed = row.Allowed,
        IsManual = row.IsManual,
        CreatedAtUtc = row.CreatedAtUtc,
        UpdatedAtUtc = row.UpdatedAtUtc,
    };

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
