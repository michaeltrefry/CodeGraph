using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Services.DatabaseSchema;

public sealed class DatabaseSchemaExtractor(
    IServiceScopeFactory scopeFactory,
    IDatabaseSourceStore sourceStore,
    ILogger<DatabaseSchemaExtractor> logger) : IDatabaseSchemaExtractor
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "performance_schema",
        "mysql",
        "sys"
    };

    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        var sources = await sourceStore.ListAsync();
        var enabled = sources.Where(source => source.Enabled).ToList();
        logger.LogInformation("Syncing {Count} database sources", enabled.Count);

        foreach (var source in enabled)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await SyncAsync(source, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to sync database source {Server}/{Database}",
                    source.ServerName,
                    source.DatabaseName);
            }
        }
    }

    public async Task SyncAsync(DatabaseSourceEntity source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.DatabaseName))
        {
            await using var conn = new MySqlConnection(source.ConnectionString);
            await conn.OpenAsync(ct);

            var schemas = await conn.QueryAsync<string>(
                "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME");
            var userDatabases = schemas.Where(schema => !SystemSchemas.Contains(schema)).ToList();

            logger.LogInformation(
                "Discovered {Count} database(s) on {Server}",
                userDatabases.Count,
                source.ServerName);

            foreach (var databaseName in userDatabases)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await SyncDatabaseAsync(source.ServerName, databaseName, source.ConnectionString, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to sync {Server}/{Database}", source.ServerName, databaseName);
                }
            }

            await sourceStore.UpdateLastSyncedAsync(source.Id);
            return;
        }

        await SyncDatabaseAsync(source.ServerName, source.DatabaseName, source.ConnectionString, ct);
        await sourceStore.UpdateLastSyncedAsync(source.Id);
    }

    private async Task SyncDatabaseAsync(
        string serverName,
        string databaseName,
        string connectionString,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var projectName = BuildProjectName(serverName, databaseName);

        logger.LogInformation("Syncing schema for {Project}", projectName);

        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = projectName,
            LocalPath = "",
            SourceGroup = serverName,
            IsFoundational = false,
            Language = "SQL",
            Framework = "MariaDB",
            Properties = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["sourceType"] = "database",
                ["serverName"] = serverName,
                ["databaseName"] = databaseName
            })
        });

        await store.DeleteAllEdgesForProjectAsync(projectName);
        await store.DeleteNodesByProjectAsync(projectName);

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var snapshot = await ExtractSnapshotAsync(conn, serverName, databaseName, projectName, ct);
        var qualifiedNameToId = await store.UpsertNodeBatchAsync(snapshot.Nodes, ct);
        var edges = ResolveEdges(projectName, snapshot.PendingEdges, qualifiedNameToId);
        await store.InsertEdgeBatchAsync(edges, ct);

        logger.LogInformation(
            "Synced {Project}: {Nodes} node(s), {Edges} edge(s)",
            projectName,
            snapshot.Nodes.Count,
            edges.Count);
    }

    private static async Task<SchemaSnapshot> ExtractSnapshotAsync(
        MySqlConnection conn,
        string serverName,
        string databaseName,
        string projectName,
        CancellationToken ct)
    {
        var databaseQualifiedName = $"{serverName}.{databaseName}";
        var nodes = new List<GraphNode>
        {
            new()
            {
                Project = projectName,
                Label = NodeLabel.Database,
                Name = databaseName,
                QualifiedName = databaseQualifiedName,
                Properties = new Dictionary<string, object>
                {
                    ["server"] = serverName,
                    ["database"] = databaseName
                }
            }
        };
        var pendingEdges = new List<PendingEdge>();

        var tables = (await conn.QueryAsync<TableInfo>("""
            SELECT TABLE_NAME, TABLE_TYPE, TABLE_COMMENT
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @databaseName
            ORDER BY TABLE_NAME
            """, new { databaseName })).ToList();

        var columns = (await conn.QueryAsync<ColumnInfo>("""
            SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_TYPE,
                   IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA, COLUMN_COMMENT
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @databaseName
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """, new { databaseName })).ToList();

        var routines = (await conn.QueryAsync<RoutineInfo>("""
            SELECT ROUTINE_NAME, ROUTINE_TYPE, ROUTINE_COMMENT
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_SCHEMA = @databaseName
            ORDER BY ROUTINE_NAME
            """, new { databaseName })).ToList();

        var routineParameters = (await conn.QueryAsync<RoutineParameterInfo>("""
            SELECT SPECIFIC_NAME, COALESCE(PARAMETER_NAME, 'return') AS PARAMETER_NAME,
                   ORDINAL_POSITION, COALESCE(PARAMETER_MODE, 'RETURN') AS PARAMETER_MODE,
                   COALESCE(DTD_IDENTIFIER, DATA_TYPE) AS DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.PARAMETERS
            WHERE SPECIFIC_SCHEMA = @databaseName
            ORDER BY SPECIFIC_NAME, ORDINAL_POSITION
            """, new { databaseName })).ToList();

        var foreignKeys = (await conn.QueryAsync<ForeignKeyInfo>("""
            SELECT CONSTRAINT_NAME, TABLE_NAME, COLUMN_NAME,
                   REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @databaseName
              AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION
            """, new { databaseName })).ToList();

        var indexes = (await conn.QueryAsync<IndexInfo>("""
            SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME, INDEX_TYPE
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @databaseName
            ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
            """, new { databaseName })).ToList();

        var columnsByTable = columns
            .GroupBy(column => column.TABLE_NAME, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var indexesByTable = indexes
            .GroupBy(index => index.TABLE_NAME, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var parametersByRoutine = routineParameters
            .GroupBy(parameter => parameter.SPECIFIC_NAME, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            var tableQualifiedName = $"{serverName}.{databaseName}.{table.TABLE_NAME}";
            var tableProperties = new Dictionary<string, object>
            {
                ["server"] = serverName,
                ["database"] = databaseName
            };

            if (!string.IsNullOrWhiteSpace(table.TABLE_COMMENT))
                tableProperties["comment"] = table.TABLE_COMMENT;
            if (indexesByTable.TryGetValue(table.TABLE_NAME, out var tableIndexes))
            {
                var indexMetadata = BuildIndexMetadata(tableIndexes);
                tableProperties["primaryKeyColumns"] = indexMetadata.PrimaryKeyColumns;
                tableProperties["indexes"] = indexMetadata.Indexes;
                tableProperties["indexCount"] = indexMetadata.Indexes.Count;
            }

            nodes.Add(new GraphNode
            {
                Project = projectName,
                Label = table.TABLE_TYPE == "VIEW" ? NodeLabel.View : NodeLabel.Table,
                Name = table.TABLE_NAME,
                QualifiedName = tableQualifiedName,
                Properties = tableProperties
            });

            pendingEdges.Add(new PendingEdge(databaseQualifiedName, tableQualifiedName, EdgeType.CONTAINS_FILE));

            if (!columnsByTable.TryGetValue(table.TABLE_NAME, out var tableColumns))
                continue;

            foreach (var column in tableColumns)
            {
                var columnQualifiedName = $"{tableQualifiedName}.{column.COLUMN_NAME}";
                var columnProperties = new Dictionary<string, object>
                {
                    ["dataType"] = column.COLUMN_TYPE,
                    ["ordinal"] = (int)column.ORDINAL_POSITION,
                    ["nullable"] = column.IS_NULLABLE == "YES",
                    ["isPrimaryKey"] = string.Equals(column.COLUMN_KEY, "PRI", StringComparison.OrdinalIgnoreCase),
                    ["server"] = serverName,
                    ["database"] = databaseName
                };

                if (!string.IsNullOrWhiteSpace(column.COLUMN_DEFAULT))
                    columnProperties["default"] = column.COLUMN_DEFAULT;
                if (!string.IsNullOrWhiteSpace(column.COLUMN_KEY))
                    columnProperties["key"] = column.COLUMN_KEY;
                if (!string.IsNullOrWhiteSpace(column.EXTRA))
                    columnProperties["extra"] = column.EXTRA;
                if (!string.IsNullOrWhiteSpace(column.COLUMN_COMMENT))
                    columnProperties["comment"] = column.COLUMN_COMMENT;

                nodes.Add(new GraphNode
                {
                    Project = projectName,
                    Label = NodeLabel.Column,
                    Name = column.COLUMN_NAME,
                    QualifiedName = columnQualifiedName,
                    Properties = columnProperties
                });

                pendingEdges.Add(new PendingEdge(tableQualifiedName, columnQualifiedName, EdgeType.HAS_COLUMN));
            }
        }

        foreach (var routine in routines)
        {
            var routineQualifiedName = $"{serverName}.{databaseName}.{routine.ROUTINE_NAME}";
            var routineProperties = new Dictionary<string, object>
            {
                ["routineType"] = routine.ROUTINE_TYPE,
                ["server"] = serverName,
                ["database"] = databaseName
            };

            if (!string.IsNullOrWhiteSpace(routine.ROUTINE_COMMENT))
                routineProperties["comment"] = routine.ROUTINE_COMMENT;
            if (parametersByRoutine.TryGetValue(routine.ROUTINE_NAME, out var parameters))
                routineProperties["parameters"] = BuildParameterMetadata(parameters);

            nodes.Add(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.StoredProcedure,
                Name = routine.ROUTINE_NAME,
                QualifiedName = routineQualifiedName,
                Properties = routineProperties
            });

            pendingEdges.Add(new PendingEdge(databaseQualifiedName, routineQualifiedName, EdgeType.CONTAINS_FILE));
        }

        foreach (var foreignKey in foreignKeys)
        {
            pendingEdges.Add(new PendingEdge(
                $"{serverName}.{databaseName}.{foreignKey.TABLE_NAME}.{foreignKey.COLUMN_NAME}",
                $"{serverName}.{databaseName}.{foreignKey.REFERENCED_TABLE_NAME}.{foreignKey.REFERENCED_COLUMN_NAME}",
                EdgeType.FOREIGN_KEY,
                new Dictionary<string, object> { ["constraintName"] = foreignKey.CONSTRAINT_NAME }));
        }

        return new SchemaSnapshot(nodes, pendingEdges);
    }

    private static List<GraphEdge> ResolveEdges(
        string projectName,
        IReadOnlyList<PendingEdge> pendingEdges,
        IReadOnlyDictionary<string, long> qualifiedNameToId)
    {
        var edges = new List<GraphEdge>();
        foreach (var pendingEdge in pendingEdges)
        {
            if (!qualifiedNameToId.TryGetValue(pendingEdge.SourceQualifiedName, out var sourceId) ||
                !qualifiedNameToId.TryGetValue(pendingEdge.TargetQualifiedName, out var targetId))
            {
                continue;
            }

            edges.Add(new GraphEdge
            {
                Project = projectName,
                SourceId = sourceId,
                TargetId = targetId,
                Type = pendingEdge.Type,
                Properties = pendingEdge.Properties ?? new Dictionary<string, object>()
            });
        }

        return edges;
    }

    private static string BuildProjectName(string serverName, string databaseName)
        => $"db:{serverName}:{databaseName}";

    private static IndexMetadata BuildIndexMetadata(IReadOnlyList<IndexInfo> indexes)
    {
        var primaryKeyColumns = indexes
            .Where(index => string.Equals(index.INDEX_NAME, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(index => index.SEQ_IN_INDEX)
            .Select(index => index.COLUMN_NAME)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToList();

        var secondaryIndexes = indexes
            .Where(index => !string.Equals(index.INDEX_NAME, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            .GroupBy(index => index.INDEX_NAME, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(index => index.SEQ_IN_INDEX).ToList();
                return (object)new Dictionary<string, object>
                {
                    ["name"] = group.Key,
                    ["isUnique"] = ordered[0].NON_UNIQUE == 0,
                    ["indexType"] = ordered[0].INDEX_TYPE,
                    ["columns"] = ordered.Select(index => index.COLUMN_NAME).ToList()
                };
            })
            .ToList();

        return new IndexMetadata(primaryKeyColumns, secondaryIndexes);
    }

    private static List<object> BuildParameterMetadata(IReadOnlyList<RoutineParameterInfo> parameters)
        => parameters
            .OrderBy(parameter => parameter.ORDINAL_POSITION)
            .Select(parameter => (object)new Dictionary<string, object>
            {
                ["name"] = parameter.PARAMETER_NAME,
                ["ordinal"] = parameter.ORDINAL_POSITION,
                ["mode"] = parameter.PARAMETER_MODE,
                ["dataType"] = parameter.DATA_TYPE,
                ["nullable"] = string.Equals(parameter.IS_NULLABLE, "YES", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

    private sealed record SchemaSnapshot(
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<PendingEdge> PendingEdges);

    private sealed record PendingEdge(
        string SourceQualifiedName,
        string TargetQualifiedName,
        EdgeType Type,
        Dictionary<string, object>? Properties = null);

    private sealed record TableInfo(string TABLE_NAME, string TABLE_TYPE, string? TABLE_COMMENT);
    private sealed record ColumnInfo(
        string TABLE_NAME,
        string COLUMN_NAME,
        ulong ORDINAL_POSITION,
        string COLUMN_TYPE,
        string IS_NULLABLE,
        string? COLUMN_DEFAULT,
        string? COLUMN_KEY,
        string? EXTRA,
        string? COLUMN_COMMENT);
    private sealed record RoutineInfo(string ROUTINE_NAME, string ROUTINE_TYPE, string? ROUTINE_COMMENT);
    private sealed record RoutineParameterInfo(
        string SPECIFIC_NAME,
        string PARAMETER_NAME,
        int ORDINAL_POSITION,
        string PARAMETER_MODE,
        string DATA_TYPE,
        string? IS_NULLABLE);
    private sealed record ForeignKeyInfo(
        string CONSTRAINT_NAME,
        string TABLE_NAME,
        string COLUMN_NAME,
        string REFERENCED_TABLE_NAME,
        string REFERENCED_COLUMN_NAME);
    private sealed record IndexInfo(
        string TABLE_NAME,
        string INDEX_NAME,
        long NON_UNIQUE,
        uint SEQ_IN_INDEX,
        string COLUMN_NAME,
        string INDEX_TYPE);
    private sealed record IndexMetadata(List<object> PrimaryKeyColumns, List<object> Indexes);
}
