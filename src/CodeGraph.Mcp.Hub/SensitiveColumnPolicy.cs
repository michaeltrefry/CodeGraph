using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Evaluates whether a parsed read-only SQL statement would expose a sensitive column, and
/// throws <see cref="McpHubProviderPolicyException"/> before the query is ever sent to the
/// database. Previously this check ran against the result reader *after* execution and only
/// inspected projected aliases, so `SELECT password AS p` slipped through — see Shortcut sc-1051.
///
/// The compiled deny list is cached and keyed on the store revision token: while
/// sensitive-column metadata is unchanged the snapshot is reused; any change to the metadata
/// rolls the revision and the next call rebuilds the snapshot.
/// </summary>
public sealed class SensitiveColumnPolicy(IServiceScopeFactory scopeFactory)
{
    private readonly object gate = new();
    private string? cachedRevision;
    private Snapshot? cachedSnapshot;

    public async Task EnsureQueryAllowedAsync(
        string sourceKey,
        ReadOnlySqlValidationResult parsed,
        CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        var source = Normalize(sourceKey);
        var tables = parsed.ReferencedTables
            .Select(table => table.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var column in parsed.ReferencedColumns)
        {
            if (snapshot.IsColumnDenied(source, tables, column))
                throw new McpHubProviderPolicyException(
                    $"Column '{column}' is marked sensitive and cannot be selected through the read-only SQL tool.");
        }

        if (parsed.HasWildcardProjection && snapshot.HasDeniedColumnForTables(source, tables))
            throw new McpHubProviderPolicyException(
                "Wildcard projections (SELECT *) are not allowed because a referenced table has sensitive columns. Select columns explicitly.");
    }

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpSensitiveColumnStore>();

        var revision = await store.GetRevisionAsync(ct);
        lock (gate)
        {
            if (cachedSnapshot is not null && cachedRevision == revision)
                return cachedSnapshot;
        }

        var rows = await store.ListAsync(ct);
        var snapshot = new Snapshot(rows);
        lock (gate)
        {
            cachedRevision = revision;
            cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "*" : value.Trim().ToLowerInvariant();

    private sealed class Snapshot(IReadOnlyList<McpSensitiveColumnEntity> rows)
    {
        public bool IsColumnDenied(string source, IReadOnlySet<string> tables, string columnName)
        {
            var column = columnName.Trim().ToLowerInvariant();
            var matching = rows
                .Where(row => row.ColumnName == column && SourceMatches(row, source) && TableMatches(row, tables))
                .ToList();

            if (matching.Count == 0)
                return false;

            // An explicit "allowed" override un-flags the column for this scope.
            return !matching.Any(row => row.Allowed);
        }

        public bool HasDeniedColumnForTables(string source, IReadOnlySet<string> tables) =>
            rows
                .Where(row => SourceMatches(row, source) && TableMatches(row, tables))
                .Select(row => row.ColumnName)
                .Distinct(StringComparer.Ordinal)
                .Any(column => IsColumnDenied(source, tables, column));

        private static bool SourceMatches(McpSensitiveColumnEntity row, string source) =>
            row.SourceKey == "*" || row.SourceKey == source;

        private static bool TableMatches(McpSensitiveColumnEntity row, IReadOnlySet<string> tables) =>
            row.TableName == "*" || tables.Contains(row.TableName);
    }
}
