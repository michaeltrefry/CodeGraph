using System.Text;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>Batched node/edge lookups and file-slice retractions.</summary>
public partial class MySqlGraphStore
{
    private const int LookupBatchSize = 1000;


    public async Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string project, IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
    {
        var result = new List<GraphNode>();
        foreach (var chunk in ExpandPathVariants(filePaths.ToList()).Chunk(LookupBatchSize))
        {
            var batch = chunk.ToList();
            var nodes = await db.Nodes.AsNoTracking()
                .Where(n => n.Project == project && batch.Contains(n.FilePath))
                .OrderBy(n => n.Id)
                .ToListAsync(ct);
            result.AddRange(nodes.Select(MapNode));
        }

        return result.DistinctBy(node => node.Id).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string project, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var result = new List<GraphNode>();
        foreach (var chunk in names.Distinct(StringComparer.Ordinal).Chunk(LookupBatchSize))
        {
            var batch = chunk.ToList();
            var nodes = await db.Nodes.AsNoTracking()
                .Where(n => n.Project == project && batch.Contains(n.Name))
                .OrderBy(n => n.Id)
                .ToListAsync(ct);
            result.AddRange(nodes.Select(MapNode));
        }

        return result.DistinctBy(node => node.Id).ToList();
    }

    public async Task<IReadOnlyList<StoredEdge>> FindEdgesByPropertyAsync(
        string project,
        IReadOnlyCollection<EdgeType> types,
        string propertyKey,
        IReadOnlyCollection<string> values,
        CancellationToken ct = default)
    {
        if (!PropertyKeyPattern.IsMatch(propertyKey))
            throw new ArgumentException($"Unsupported edge property key '{propertyKey}'.", nameof(propertyKey));
        if (types.Count == 0)
            return [];

        // (project, type) narrows the scan through idx_edges_type before any JSON is inspected.
        var typeNames = types.Distinct().Select(type => type.ToString()).ToArray();

        await using var conn = await GetOpenConnectionAsync();
        var result = new List<StoredEdge>();
        foreach (var batch in values.Distinct(StringComparer.Ordinal).Chunk(LookupBatchSize))
        {
            var rows = await conn.QueryAsync<StoredEdgeRow>(new CommandDefinition($"""
                SELECT source.qualified_name AS SourceQN,
                       source.file_path AS SourceFilePath,
                       target.qualified_name AS TargetQN,
                       target.file_path AS TargetFilePath,
                       edge.type AS Type,
                       edge.properties AS Properties
                FROM edges edge
                INNER JOIN nodes source ON source.id = edge.source_id
                INNER JOIN nodes target ON target.id = edge.target_id
                WHERE edge.project = @Project
                  AND edge.type IN @Types
                  AND JSON_UNQUOTE(JSON_EXTRACT(edge.properties, '$."{propertyKey}"')) IN @Values
                """, new { Project = project, Types = typeNames, Values = batch }, cancellationToken: ct));
            foreach (var row in rows)
            {
                if (!Enum.TryParse<EdgeType>(row.Type, out var type))
                    continue;
                result.Add(new StoredEdge(
                    row.SourceQN, row.SourceFilePath, row.TargetQN, row.TargetFilePath, type,
                    DeserializeJson(row.Properties) ?? new()));
            }
        }

        return result;
    }

    private async Task DeleteRetractedEdgesAsync(
        MySqlConnection conn,
        MySqlTransaction transaction,
        string project,
        IReadOnlyList<PendingEdge> edges,
        CancellationToken ct)
    {
        var qualifiedNames = edges.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var qnToId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var batch in qualifiedNames.Chunk(LookupBatchSize))
        {
            var lookup = new DynamicParameters(new { Project = project });
            var rows = await conn.QueryAsync<(long Id, string QualifiedName)>(new CommandDefinition(
                $"SELECT id AS Id, qualified_name AS QualifiedName FROM nodes WHERE project = @Project AND {QualifiedNameMatch(batch, lookup)}",
                lookup, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            foreach (var row in rows)
                qnToId[row.QualifiedName] = row.Id;
        }

        var keys = edges
            .Where(edge => qnToId.ContainsKey(edge.SourceQN) && qnToId.ContainsKey(edge.TargetQN))
            .Select(edge => (Source: qnToId[edge.SourceQN], Target: qnToId[edge.TargetQN], Type: edge.Type.ToString()))
            .Distinct()
            .ToList();
        foreach (var batch in keys.Chunk(Math.Max(1, options.GraphDeleteBatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            var sql = new StringBuilder("DELETE FROM edges WHERE project = @Project AND (");
            var parameters = new DynamicParameters(new { Project = project });
            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0) sql.Append(" OR ");
                sql.Append($"(source_id = @S{i} AND target_id = @T{i} AND type = @Ty{i})");
                parameters.Add($"S{i}", batch[i].Source);
                parameters.Add($"T{i}", batch[i].Target);
                parameters.Add($"Ty{i}", batch[i].Type);
            }

            sql.Append(')');
            await conn.ExecuteAsync(new CommandDefinition(
                sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
        }
    }

    private async Task DeleteOrphanedNodesAsync(
        MySqlConnection conn,
        MySqlTransaction transaction,
        string project,
        IReadOnlyList<string> qualifiedNames,
        CancellationToken ct)
    {
        var orphanIds = new List<long>();
        foreach (var batch in qualifiedNames.Distinct(StringComparer.Ordinal).Chunk(LookupBatchSize))
        {
            var lookup = new DynamicParameters(new { Project = project });
            orphanIds.AddRange(await conn.QueryAsync<long>(new CommandDefinition($"""
                SELECT n.id FROM nodes n
                WHERE n.project = @Project AND {QualifiedNameMatch(batch, lookup)}
                  AND (n.file_path IS NULL OR n.file_path = '')
                  AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.source_id = n.id)
                  AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.target_id = n.id)
                """, lookup, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct)));
        }

        if (orphanIds.Count > 0)
            await DeleteNodesInBatchesAsync(conn, transaction, orphanIds, ct);
    }

    private sealed record StoredEdgeRow(
        string SourceQN,
        string? SourceFilePath,
        string TargetQN,
        string? TargetFilePath,
        string Type,
        string? Properties);
}
