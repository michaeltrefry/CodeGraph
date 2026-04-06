using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── Edges — simple queries via EF, batch insert via Dapper ────────────

    public async Task InsertEdgeAsync(GraphEdge edge)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO edges (project, source_id, target_id, type, properties)
            VALUES (@Project, @SourceId, @TargetId, @Type, @Properties)
            ON DUPLICATE KEY UPDATE properties = VALUES(properties)
            """,
            new
            {
                edge.Project,
                edge.SourceId,
                edge.TargetId,
                Type = edge.Type.ToString(),
                Properties = SerializeJson(edge.Properties)
            });
    }

    public async Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(edges, options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO edges (project, source_id, target_id, type, properties)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@Project{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Props{i})");

                var e = batch[i];
                parameters.Add($"Project{i}", e.Project);
                parameters.Add($"SourceId{i}", e.SourceId);
                parameters.Add($"TargetId{i}", e.TargetId);
                parameters.Add($"Type{i}", e.Type.ToString());
                parameters.Add($"Props{i}", SerializeJson(e.Properties));
            }

            sb.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");

            var sql = sb.ToString();
            await WithDeadlockRetryAsync(async () => await conn.ExecuteAsync(sql, parameters));
        }
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = context.Edges.AsNoTracking().Where(e => e.SourceId == sourceId);
        if (typeStr is not null)
            query = query.Where(e => e.Type == typeStr);
        return await query.Select(e => MapEdgeEntity(e)).ToListAsync();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = context.Edges.AsNoTracking().Where(e => e.TargetId == targetId);
        if (typeStr is not null)
            query = query.Where(e => e.Type == typeStr);
        return await query.Select(e => MapEdgeEntity(e)).ToListAsync();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        if (targetIds.Count == 0) return [];
        var typeStrs = types?.Select(t => t.ToString()).ToList();
        var query = context.Edges.AsNoTracking().Where(e => targetIds.Contains(e.TargetId));
        if (typeStrs is { Count: > 0 })
            query = query.Where(e => typeStrs.Contains(e.Type));
        return await query.Select(e => MapEdgeEntity(e)).ToListAsync();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type)
    {
        var typeStr = type.ToString();
        return await context.Edges
            .AsNoTracking()
            .Where(e => e.Type == typeStr)
            .Select(e => MapEdgeEntity(e))
            .ToListAsync();
    }

    public async Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync()
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<(string type, int count)>(
            "SELECT type, COUNT(*) AS count FROM edges GROUP BY type");
        return rows
            .Where(r => Enum.TryParse<EdgeType>(r.type, out _))
            .ToDictionary(r => Enum.Parse<EdgeType>(r.type), r => r.count);
    }

    public async Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<(long target_id, int count)>("""
            SELECT e.target_id, COUNT(*) AS count
            FROM edges e
            JOIN nodes n ON n.id = e.target_id
            WHERE e.type = 'CALLS' AND n.project = @Project
            GROUP BY e.target_id
            HAVING COUNT(*) >= @MinFanIn
            """,
            new { Project = project, MinFanIn = minFanIn });
        return rows.ToDictionary(r => r.target_id, r => r.count);
    }

    public async Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync()
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<string>("""
            SELECT p.name FROM repositories p
            WHERE NOT EXISTS (
                SELECT 1 FROM cross_repo_edges cre
                WHERE cre.source_project = p.name OR cre.target_project = p.name
            )
            ORDER BY p.name
            """);
        return rows.ToList();
    }

    // ── Cross-Repo Edges (EF for simple, Dapper for batch) ────────────────

    public async Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO cross_repo_edges (source_project, target_project, source_node_id, target_node_id, type, properties)
            VALUES (@SourceProject, @TargetProject, @SourceNodeId, @TargetNodeId, @Type, @Properties)
            ON DUPLICATE KEY UPDATE properties = VALUES(properties)
            """,
            new
            {
                edge.SourceProject,
                edge.TargetProject,
                edge.SourceNodeId,
                edge.TargetNodeId,
                Type = edge.Type.ToString(),
                Properties = SerializeJson(edge.Properties)
            });
    }

    public async Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(edges, options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO cross_repo_edges (source_project, target_project, source_node_id, target_node_id, type, properties)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@SrcProj{i}, @TgtProj{i}, @SrcNode{i}, @TgtNode{i}, @Type{i}, @Props{i})");

                var e = batch[i];
                parameters.Add($"SrcProj{i}", e.SourceProject);
                parameters.Add($"TgtProj{i}", e.TargetProject);
                parameters.Add($"SrcNode{i}", e.SourceNodeId);
                parameters.Add($"TgtNode{i}", e.TargetNodeId);
                parameters.Add($"Type{i}", e.Type.ToString());
                parameters.Add($"Props{i}", SerializeJson(e.Properties));
            }

            sb.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");

            await WithDeadlockRetryAsync(async () => await conn.ExecuteAsync(sb.ToString(), parameters));
        }
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = context.CrossRepoEdges
            .AsNoTracking()
            .Where(e => e.SourceProject == project || e.TargetProject == project);
        if (typeStr is not null)
            query = query.Where(e => e.Type == typeStr);

        return await query.Select(e => new CrossRepoEdge
        {
            Id = e.Id,
            SourceProject = e.SourceProject,
            TargetProject = e.TargetProject,
            SourceNodeId = e.SourceNodeId,
            TargetNodeId = e.TargetNodeId,
            Type = Enum.Parse<EdgeType>(e.Type),
            Properties = DeserializeJson(e.Properties) ?? new()
        }).ToListAsync();
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync()
    {
        return await context.CrossRepoEdges
            .AsNoTracking()
            .Select(e => new CrossRepoEdge
            {
                Id = e.Id,
                SourceProject = e.SourceProject,
                TargetProject = e.TargetProject,
                SourceNodeId = e.SourceNodeId,
                TargetNodeId = e.TargetNodeId,
                Type = Enum.Parse<EdgeType>(e.Type),
                Properties = DeserializeJson(e.Properties) ?? new()
            }).ToListAsync();
    }

    // ── Traversal (Dapper — recursive CTEs) ───────────────────────────────

    public async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0)
    {
        await using var conn = await GetOpenConnectionAsync();

        var edgeFilterClause = edgeFilter is { Length: > 0 }
            ? $"AND e.type IN ({string.Join(",", edgeFilter.Select((_, i) => $"@ef{i}"))})"
            : "";

        var parameters = new DynamicParameters();
        parameters.Add("startId", startNodeId);
        parameters.Add("maxDepth", maxDepth);

        if (edgeFilter is { Length: > 0 })
        {
            for (int i = 0; i < edgeFilter.Length; i++)
                parameters.Add($"ef{i}", edgeFilter[i].ToString());
        }

        string directionJoin = direction switch
        {
            TraceDirection.Outbound => "e.source_id = t.node_id",
            TraceDirection.Inbound => "e.target_id = t.node_id",
            TraceDirection.Both => "(e.source_id = t.node_id OR e.target_id = t.node_id)",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        string seedSelect = direction switch
        {
            TraceDirection.Outbound => "e.target_id AS node_id, e.source_id AS parent_id",
            TraceDirection.Inbound => "e.source_id AS node_id, e.target_id AS parent_id",
            TraceDirection.Both => "IF(e.source_id = @startId, e.target_id, e.source_id) AS node_id, @startId AS parent_id",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        string seedWhere = direction switch
        {
            TraceDirection.Outbound => "e.source_id = @startId",
            TraceDirection.Inbound => "e.target_id = @startId",
            TraceDirection.Both => "(e.source_id = @startId OR e.target_id = @startId)",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        string recurseSelect = direction switch
        {
            TraceDirection.Outbound => "e.target_id, e.source_id",
            TraceDirection.Inbound => "e.source_id, e.target_id",
            TraceDirection.Both => "IF(e.source_id = t.node_id, e.target_id, e.source_id), t.node_id",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        var sql = $"""
            WITH RECURSIVE traversal AS (
                SELECT {seedSelect},
                       1 AS depth, e.type, e.properties AS edge_properties
                FROM edges e
                WHERE {seedWhere}
                  {edgeFilterClause}
                UNION ALL
                SELECT {recurseSelect},
                       t.depth + 1, e.type, e.properties
                FROM edges e
                JOIN traversal t ON {directionJoin}
                WHERE t.depth < @maxDepth
                  {edgeFilterClause}
            )
            SELECT DISTINCT n.id, n.project, n.dotnet_project, n.label, n.name, n.qualified_name,
                   n.file_path, n.start_line, n.end_line, n.properties,
                   t.depth, t.type AS edge_type,
                   t.parent_id AS parent_node_id, t.edge_properties
            FROM traversal t
            JOIN nodes n ON n.id = t.node_id
            ORDER BY t.depth, n.name
            """;

        var rows = await conn.QueryAsync<dynamic>(sql, parameters);

        return rows.Select(r => new TraversalEntry(
            MapNodeDynamic(r),
            (int)r.depth,
            Enum.Parse<EdgeType>((string)r.edge_type),
            (long?)r.parent_node_id,
            DeserializeJson((string?)r.edge_properties)
        )).ToList();
    }

    // ── Bulk Operations (Dapper) ──────────────────────────────────────────

    public async Task DeleteNodesByFileAsync(string project, string filePath)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM nodes WHERE project = @Project AND file_path = @FilePath",
            new { Project = project, FilePath = filePath });
    }

    public async Task DeleteNodesByProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM nodes WHERE project = @Project",
            new { Project = project });
    }

    // ── File Hashes (EF Core for reads, Dapper for batch upsert) ──────────

    public async Task<Dictionary<string, string>> GetFileHashesAsync(string project)
    {
        return await context.FileHashes
            .AsNoTracking()
            .Where(f => f.Project == project)
            .ToDictionaryAsync(f => f.RelPath, f => f.ContentHash);
    }

    public async Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0) return;

        await using var conn = await GetOpenConnectionAsync();

        var items = hashes.ToList();
        foreach (var batch in Chunk(items, options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO file_hashes (project, rel_path, content_hash)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@Project{i}, @RelPath{i}, @Hash{i})");

                parameters.Add($"Project{i}", project);
                parameters.Add($"RelPath{i}", batch[i].Key);
                parameters.Add($"Hash{i}", batch[i].Value);
            }

            sb.AppendLine("ON DUPLICATE KEY UPDATE content_hash = VALUES(content_hash)");

            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths)
    {
        if (relPaths.Count == 0) return;

        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM file_hashes WHERE project = @Project AND rel_path IN @RelPaths",
            new { Project = project, RelPaths = relPaths });
    }
}
