using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public class MySqlGraphStore(
    CodeGraphDbContext db,
    IOptions<MariaDbStorageOptions> optionsAccessor,
    ILogger<MySqlGraphStore> logger,
    IAnalysisStore analysisStore,
    IMetricsStore metricsStore,
    IReviewStore reviewStore,
    IMigrationRunner migrationRunner)
    : IGraphStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    static MySqlGraphStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Task ApplyMigrationsAsync(string migrationsPath)
        => migrationRunner.ApplyMigrationsAsync(migrationsPath);

    public async Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        var existing = await db.Repositories.FindAsync(repository.Name);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            repository.CreatedAt = repository.CreatedAt == default ? now : repository.CreatedAt;
            repository.UpdatedAt = now;
            repository.IndexedAt ??= now;
            db.Repositories.Add(repository);
        }
        else
        {
            existing.RepoUrl = repository.RepoUrl ?? existing.RepoUrl;
            existing.SourceGroup = repository.SourceGroup ?? existing.SourceGroup;
            existing.LocalPath = repository.LocalPath ?? existing.LocalPath;
            existing.DefaultBranch = repository.DefaultBranch ?? existing.DefaultBranch;
            existing.LastCommitSha = repository.LastCommitSha ?? existing.LastCommitSha;
            existing.IndexedAt = repository.IndexedAt ?? now;
            existing.Language = repository.Language ?? existing.Language;
            existing.Framework = repository.Framework ?? existing.Framework;
            existing.IsFoundational = repository.IsFoundational;
            existing.Properties = repository.Properties ?? existing.Properties;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync()
    {
        var repositories = await db.Repositories.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();

        return repositories.Select(MapProjectInfo).ToList();
    }

    public async Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25)
    {
        var query = db.Repositories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(group))
        {
            query = query.Where(p => p.SourceGroup == group);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{search}%"));
        }

        var total = await query.CountAsync();
        var repositories = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new RepositorySearchResult(repositories.Select(MapProjectInfo).ToList(), total);
    }

    public async Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
        => await db.Repositories.AsNoTracking()
            .Where(p => p.SourceGroup != null && p.SourceGroup != "")
            .Select(p => p.SourceGroup!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();

    public async Task<ProjectInfo?> GetRepositoryByName(string name)
    {
        var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name);
        return repository is null ? null : MapProjectInfo(repository);
    }

    public async Task UpdateRepositoryCommitShaAsync(string name, string? commitSha)
    {
        var repository = await db.Repositories.FindAsync(name);
        if (repository is null)
        {
            db.Repositories.Add(new RepositoryEntity
            {
                Name = name,
                LastCommitSha = commitSha,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            repository.LastCommitSha = commitSha;
            repository.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteRepositoryAsync(string project)
    {
        var repository = await db.Repositories.FindAsync(project);
        if (repository is null)
        {
            return;
        }

        db.Repositories.Remove(repository);
        await db.SaveChangesAsync();
    }

    public async Task<long> UpsertNodeAsync(GraphNode node)
    {
        var ids = await UpsertNodeBatchAsync([node]);
        return ids.TryGetValue(Truncate(node.QualifiedName, 1000), out var id) ? id : 0;
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken ct = default)
    {
        if (nodes.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        var result = new Dictionary<string, long>(nodes.Count, StringComparer.OrdinalIgnoreCase);
        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in nodes.Chunk(options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var sql = new StringBuilder("""
                INSERT INTO nodes (project, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties, do_not_trust)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Project{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i}, @DoNotTrust{i})");
                var node = batch[i];
                parameters.Add($"Project{i}", node.Project);
                parameters.Add($"DotnetProject{i}", node.DotnetProject);
                parameters.Add($"Label{i}", node.Label.ToString());
                parameters.Add($"Name{i}", Truncate(node.Name, 1000));
                parameters.Add($"QualifiedName{i}", Truncate(node.QualifiedName, 1000));
                parameters.Add($"FilePath{i}", node.FilePath);
                parameters.Add($"StartLine{i}", node.StartLine);
                parameters.Add($"EndLine{i}", node.EndLine);
                parameters.Add($"Properties{i}", SerializeJson(node.Properties));
                parameters.Add($"DoNotTrust{i}", node.DoNotTrust);
            }

            sql.AppendLine("""
                ON DUPLICATE KEY UPDATE
                    dotnet_project = VALUES(dotnet_project),
                    label = VALUES(label),
                    name = VALUES(name),
                    file_path = VALUES(file_path),
                    start_line = VALUES(start_line),
                    end_line = VALUES(end_line),
                    properties = VALUES(properties),
                    do_not_trust = VALUES(do_not_trust)
                """);

            await WithDeadlockRetryAsync(() => conn.ExecuteAsync(sql.ToString(), parameters));

            var projects = batch.Select(n => n.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var qualifiedNames = batch.Select(n => Truncate(n.QualifiedName, 1000)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rows = await conn.QueryAsync<(long id, string qualified_name)>(
                "SELECT id, qualified_name FROM nodes WHERE project IN @Projects AND qualified_name IN @QualifiedNames",
                new { Projects = projects, QualifiedNames = qualifiedNames });

            foreach (var row in rows)
            {
                result[row.qualified_name] = row.id;
            }
        }

        return result;
    }

    public async Task<GraphNode?> FindNodeByIdAsync(long id)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        return node is null ? null : MapNode(node);
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
    {
        var truncatedQualifiedName = Truncate(qualifiedName, 1000);
        var node = await db.Nodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Project == project && n.QualifiedName == truncatedQualifiedName);

        return node is null ? null : MapNode(node);
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000)
    {
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project && n.Name == name)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000)
    {
        var labelName = label.ToString();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project && n.Label == labelName)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000)
    {
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project && n.FilePath == filePath)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? project,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        int limit = 50,
        int offset = 0,
        string? dotnetProject = null)
    {
        var query = db.Nodes.AsNoTracking()
            .Where(n => EF.Functions.Like(n.Name, $"%{namePattern}%"));

        query = ApplyNodeSearchFilters(query, project, label, filePattern, dotnetProject);

        var nodes = await query
            .OrderBy(n => n.Name)
            .ThenBy(n => n.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public Task<int> SearchNodesCountAsync(
        string? project,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        string? dotnetProject = null)
    {
        var query = db.Nodes.AsNoTracking()
            .Where(n => EF.Functions.Like(n.Name, $"%{namePattern}%"));

        return ApplyNodeSearchFilters(query, project, label, filePattern, dotnetProject).CountAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000)
    {
        var labelName = label.ToString();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Label == labelName)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync()
    {
        var rows = await db.Nodes.AsNoTracking()
            .GroupBy(n => n.Label)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToListAsync();

        return rows.ToDictionary(r => Enum.Parse<NodeLabel>(r.Label), r => r.Count);
    }

    public async Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<long, GraphNode>();
        }

        var result = new Dictionary<long, GraphNode>(ids.Count);
        foreach (var batch in ids.Chunk(1000))
        {
            var batchIds = batch.ToList();
            var nodes = await db.Nodes.AsNoTracking()
                .Where(n => batchIds.Contains(n.Id))
                .ToListAsync();

            foreach (var node in nodes)
            {
                result[node.Id] = MapNode(node);
            }
        }

        return result;
    }

    public async Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project && n.DotnetProject != null)
            .GroupBy(n => new { n.DotnetProject, n.Label })
            .Select(g => new { g.Key.DotnetProject, g.Key.Label, Count = g.Count() })
            .ToListAsync();

        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var dotnetProject = row.DotnetProject!;
            if (!result.TryGetValue(dotnetProject, out var labelCounts))
            {
                labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                result[dotnetProject] = labelCounts;
            }

            labelCounts[row.Label] = row.Count;
        }

        return result;
    }

    public async Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project)
            .GroupBy(n => n.Label)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToListAsync();

        return rows.ToDictionary(r => r.Label, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        var node = await db.Nodes.FindAsync(nodeId);
        if (node is null)
        {
            return;
        }

        node.DoNotTrust = doNotTrust;
        await db.SaveChangesAsync();
    }

    public Task InsertEdgeAsync(GraphEdge edge)
        => InsertEdgeBatchAsync([edge]);

    public async Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0)
        {
            return;
        }

        await using var conn = await GetOpenConnectionAsync();
        foreach (var batch in edges.Chunk(options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var sql = new StringBuilder("""
                INSERT INTO edges (project, source_id, target_id, type, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Project{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Properties{i})");
                var edge = batch[i];
                parameters.Add($"Project{i}", edge.Project);
                parameters.Add($"SourceId{i}", edge.SourceId);
                parameters.Add($"TargetId{i}", edge.TargetId);
                parameters.Add($"Type{i}", edge.Type.ToString());
                parameters.Add($"Properties{i}", SerializeJson(edge.Properties));
            }

            sql.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");
            await WithDeadlockRetryAsync(() => conn.ExecuteAsync(sql.ToString(), parameters));
        }
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
    {
        var query = db.Edges.AsNoTracking().Where(e => e.SourceId == sourceId);
        if (type is not null)
        {
            var typeName = type.Value.ToString();
            query = query.Where(e => e.Type == typeName);
        }

        var edges = await query.OrderBy(e => e.Id).ToListAsync();
        return edges.Select(MapEdge).ToList();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
    {
        var query = db.Edges.AsNoTracking().Where(e => e.TargetId == targetId);
        if (type is not null)
        {
            var typeName = type.Value.ToString();
            query = query.Where(e => e.Type == typeName);
        }

        var edges = await query.OrderBy(e => e.Id).ToListAsync();
        return edges.Select(MapEdge).ToList();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        if (targetIds.Count == 0)
        {
            return [];
        }

        var typeNames = types?.Select(t => t.ToString()).ToList();
        var result = new List<GraphEdge>();
        foreach (var batch in targetIds.Chunk(1000))
        {
            var batchIds = batch.ToList();
            var query = db.Edges.AsNoTracking().Where(e => batchIds.Contains(e.TargetId));
            if (typeNames is { Count: > 0 })
            {
                query = query.Where(e => typeNames.Contains(e.Type));
            }

            result.AddRange((await query.ToListAsync()).Select(MapEdge));
        }

        return result;
    }

    public async Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type)
    {
        var typeName = type.ToString();
        var edges = await db.Edges.AsNoTracking()
            .Where(e => e.Type == typeName)
            .OrderBy(e => e.Id)
            .ToListAsync();

        return edges.Select(MapEdge).ToList();
    }

    public async Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync()
    {
        var rows = await db.Edges.AsNoTracking()
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync();

        return rows.ToDictionary(r => Enum.Parse<EdgeType>(r.Type), r => r.Count);
    }

    public Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
        => InsertCrossRepoEdgeBatchAsync([edge]);

    public async Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0)
        {
            return;
        }

        await using var conn = await GetOpenConnectionAsync();
        foreach (var batch in edges.Chunk(options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var sql = new StringBuilder("""
                INSERT INTO cross_repo_edges (source_project, target_project, source_node_id, target_node_id, type, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@SourceProject{i}, @TargetProject{i}, @SourceNodeId{i}, @TargetNodeId{i}, @Type{i}, @Properties{i})");
                var edge = batch[i];
                parameters.Add($"SourceProject{i}", edge.SourceProject);
                parameters.Add($"TargetProject{i}", edge.TargetProject);
                parameters.Add($"SourceNodeId{i}", edge.SourceNodeId);
                parameters.Add($"TargetNodeId{i}", edge.TargetNodeId);
                parameters.Add($"Type{i}", edge.Type.ToString());
                parameters.Add($"Properties{i}", SerializeJson(edge.Properties));
            }

            sql.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");
            await WithDeadlockRetryAsync(() => conn.ExecuteAsync(sql.ToString(), parameters));
        }
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string project, EdgeType? type = null)
    {
        var query = db.CrossRepoEdges.AsNoTracking()
            .Where(e => e.SourceProject == project || e.TargetProject == project);
        if (type is not null)
        {
            var typeName = type.Value.ToString();
            query = query.Where(e => e.Type == typeName);
        }

        var edges = await query.OrderBy(e => e.Id).ToListAsync();
        return edges.Select(MapCrossRepoEdge).ToList();
    }

    public async Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync()
    {
        var projects = await db.Repositories.AsNoTracking()
            .Where(p => !db.CrossRepoEdges.Any(e => e.SourceProject == p.Name || e.TargetProject == p.Name))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

        return projects;
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync()
    {
        var edges = await db.CrossRepoEdges.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        return edges.Select(MapCrossRepoEdge).ToList();
    }

    public async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(
        long startNodeId,
        TraceDirection direction,
        int maxDepth,
        EdgeType[]? edgeFilter = null,
        double minConfidence = 0)
    {
        await using var conn = await GetOpenConnectionAsync();

        var edgeFilterClause = edgeFilter is { Length: > 0 }
            ? $"AND e.type IN ({string.Join(",", edgeFilter.Select((_, i) => $"@EdgeType{i}"))})"
            : "";

        var confidenceClause = minConfidence > 0
            ? "AND (JSON_EXTRACT(e.properties, '$.confidence') IS NULL OR CAST(JSON_UNQUOTE(JSON_EXTRACT(e.properties, '$.confidence')) AS DECIMAL(10,4)) >= @MinConfidence)"
            : "";

        var directionJoin = direction switch
        {
            TraceDirection.Outbound => "e.source_id = traversal.node_id",
            TraceDirection.Inbound => "e.target_id = traversal.node_id",
            TraceDirection.Both => "(e.source_id = traversal.node_id OR e.target_id = traversal.node_id)",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        var seedSelect = direction switch
        {
            TraceDirection.Outbound => "e.target_id AS node_id, e.source_id AS parent_id",
            TraceDirection.Inbound => "e.source_id AS node_id, e.target_id AS parent_id",
            TraceDirection.Both => "IF(e.source_id = @StartNodeId, e.target_id, e.source_id) AS node_id, @StartNodeId AS parent_id",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        var seedWhere = direction switch
        {
            TraceDirection.Outbound => "e.source_id = @StartNodeId",
            TraceDirection.Inbound => "e.target_id = @StartNodeId",
            TraceDirection.Both => "(e.source_id = @StartNodeId OR e.target_id = @StartNodeId)",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        var recurseSelect = direction switch
        {
            TraceDirection.Outbound => "e.target_id, e.source_id",
            TraceDirection.Inbound => "e.source_id, e.target_id",
            TraceDirection.Both => "IF(e.source_id = traversal.node_id, e.target_id, e.source_id), traversal.node_id",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        var parameters = new DynamicParameters();
        parameters.Add("StartNodeId", startNodeId);
        parameters.Add("MaxDepth", maxDepth);
        parameters.Add("MinConfidence", minConfidence);
        if (edgeFilter is { Length: > 0 })
        {
            for (var i = 0; i < edgeFilter.Length; i++)
            {
                parameters.Add($"EdgeType{i}", edgeFilter[i].ToString());
            }
        }

        var sql = $"""
            WITH RECURSIVE traversal AS (
                SELECT {seedSelect}, 1 AS depth, e.type, e.properties AS edge_properties
                FROM edges e
                WHERE {seedWhere}
                  {edgeFilterClause}
                  {confidenceClause}
                UNION ALL
                SELECT {recurseSelect}, traversal.depth + 1, e.type, e.properties
                FROM edges e
                JOIN traversal ON {directionJoin}
                WHERE traversal.depth < @MaxDepth
                  {edgeFilterClause}
                  {confidenceClause}
            )
            SELECT DISTINCT
                n.id,
                n.project,
                n.dotnet_project,
                n.label,
                n.name,
                n.qualified_name,
                n.file_path,
                n.start_line,
                n.end_line,
                n.properties,
                n.do_not_trust,
                traversal.depth,
                traversal.type AS edge_type,
                traversal.parent_id AS parent_node_id,
                traversal.edge_properties
            FROM traversal
            JOIN nodes n ON n.id = traversal.node_id
            ORDER BY traversal.depth, n.name
            """;

        var rows = await conn.QueryAsync<TraversalRow>(sql, parameters);
        return rows.Select(r => new TraversalEntry(
            MapNode(r),
            r.Depth,
            Enum.Parse<EdgeType>(r.EdgeType),
            r.ParentNodeId,
            DeserializeJson(r.EdgeProperties))).ToList();
    }

    public async Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn)
    {
        var rows = await db.Edges.AsNoTracking()
            .Where(e => e.Type == EdgeType.CALLS.ToString() && e.Project == project)
            .GroupBy(e => e.TargetId)
            .Where(g => g.Count() >= minFanIn)
            .Select(g => new { TargetId = g.Key, Count = g.Count() })
            .ToListAsync();

        return rows.ToDictionary(r => r.TargetId, r => r.Count);
    }

    public async Task DeleteNodesByFileAsync(string project, string filePath)
    {
        var nodes = await db.Nodes.Where(n => n.Project == project && n.FilePath == filePath).ToListAsync();
        db.Nodes.RemoveRange(nodes);
        await db.SaveChangesAsync();
    }

    public async Task DeleteNodesByProjectAsync(string project)
    {
        var nodes = await db.Nodes.Where(n => n.Project == project).ToListAsync();
        db.Nodes.RemoveRange(nodes);
        await db.SaveChangesAsync();
    }

    public async Task<Dictionary<string, string>> GetFileHashesAsync(string project)
        => await db.FileHashes.AsNoTracking()
            .Where(f => f.Project == project)
            .ToDictionaryAsync(f => f.RelPath, f => f.ContentHash, StringComparer.OrdinalIgnoreCase);

    public async Task UpsertFileHashBatchAsync(
        string project,
        Dictionary<string, string> hashes,
        CancellationToken ct = default)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        await using var conn = await GetOpenConnectionAsync();
        foreach (var batch in hashes.ToArray().Chunk(options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var sql = new StringBuilder("""
                INSERT INTO file_hashes (project, rel_path, content_hash)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Project{i}, @RelPath{i}, @ContentHash{i})");
                parameters.Add($"Project{i}", project);
                parameters.Add($"RelPath{i}", batch[i].Key);
                parameters.Add($"ContentHash{i}", batch[i].Value);
            }

            sql.AppendLine("ON DUPLICATE KEY UPDATE content_hash = VALUES(content_hash)");
            await conn.ExecuteAsync(sql.ToString(), parameters);
        }
    }

    public async Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths)
    {
        if (relPaths.Count == 0)
        {
            return;
        }

        foreach (var batch in relPaths.Chunk(1000))
        {
            var batchPaths = batch.ToList();
            var hashes = await db.FileHashes
                .Where(f => f.Project == project && batchPaths.Contains(f.RelPath))
                .ToListAsync();
            db.FileHashes.RemoveRange(hashes);
        }

        await db.SaveChangesAsync();
    }

    public Task<SyncStateEntity?> GetSyncStateAsync(string project)
        => db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Project == project);

    public async Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> projects)
    {
        if (projects.Count == 0)
        {
            return new Dictionary<string, SyncStateEntity>();
        }

        var states = await db.SyncStates.AsNoTracking()
            .Where(s => projects.Contains(s.Project))
            .ToListAsync();

        return states.ToDictionary(s => s.Project, StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        var existing = await db.SyncStates.FindAsync(state.Project);
        if (existing is null)
        {
            db.SyncStates.Add(state);
        }
        else
        {
            existing.LastSyncAt = state.LastSyncAt;
            existing.LastCommitSha = state.LastCommitSha ?? existing.LastCommitSha;
            existing.Status = state.Status;
            existing.ErrorMessage = state.ErrorMessage;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteSyncStateAsync(string project)
    {
        var state = await db.SyncStates.FindAsync(project);
        if (state is null)
        {
            return;
        }

        db.SyncStates.Remove(state);
        await db.SaveChangesAsync();
    }

    public async Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM repo_clusters");

        if (clusters.Count == 0)
        {
            return;
        }

        foreach (var batch in clusters.Chunk(options.BatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO repo_clusters (project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@ProjectName{i}, @ClusterId{i}, @ClusterLabel{i}, @ModularityScore{i}, @Level{i}, @BetweennessCentrality{i}, @ComputedAt{i})");
                var cluster = batch[i];
                parameters.Add($"ProjectName{i}", cluster.ProjectName);
                parameters.Add($"ClusterId{i}", cluster.ClusterId);
                parameters.Add($"ClusterLabel{i}", cluster.ClusterLabel);
                parameters.Add($"ModularityScore{i}", cluster.ModularityScore);
                parameters.Add($"Level{i}", cluster.Level);
                parameters.Add($"BetweennessCentrality{i}", cluster.BetweennessCentrality);
                parameters.Add($"ComputedAt{i}", cluster.ComputedAt);
            }

            await conn.ExecuteAsync(sql.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<RepoClusterRow>("""
            SELECT id, project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at
            FROM repo_clusters
            WHERE level = @Level
            ORDER BY cluster_id, project_name
            """, new { Level = level });

        return rows.Select(MapRepoCluster).ToList();
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<RepoClusterRow>("""
            SELECT id, project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at
            FROM repo_clusters
            WHERE cluster_id = @ClusterId AND level = @Level
            ORDER BY betweenness_centrality DESC
            """, new { ClusterId = clusterId, Level = level });

        return rows.Select(MapRepoCluster).ToList();
    }

    public async Task DeleteAllEdgesForProjectAsync(string project)
    {
        var edges = await db.Edges.Where(e => e.Project == project).ToListAsync();
        db.Edges.RemoveRange(edges);
        await db.SaveChangesAsync();
    }

    public async Task DeleteCrossRepoEdgesForProjectAsync(string project)
    {
        var edges = await db.CrossRepoEdges
            .Where(e => e.SourceProject == project || e.TargetProject == project)
            .ToListAsync();
        db.CrossRepoEdges.RemoveRange(edges);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAnalysisDataForProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("""
            DELETE br FROM analysis_batch_requests br
            INNER JOIN analysis_batches b ON br.batch_id = b.id
            WHERE b.repo = @Project
            """, new { Project = project });
        await conn.ExecuteAsync("DELETE FROM analysis_batches WHERE repo = @Project", new { Project = project });
        await conn.ExecuteAsync("DELETE FROM project_analyses WHERE repo = @Project", new { Project = project });
        await conn.ExecuteAsync("DELETE FROM repository_summaries WHERE project = @Project", new { Project = project });
        await conn.ExecuteAsync("""
            DELETE na FROM node_analysis na
            INNER JOIN nodes n ON n.id = na.node_id
            WHERE n.project = @Project
            """, new { Project = project });
        await conn.ExecuteAsync("DELETE FROM project_health_analyses WHERE project = @Project", new { Project = project });
        await conn.ExecuteAsync("DELETE FROM project_health_summaries WHERE project = @Project", new { Project = project });
    }

    public Task UpsertRepositorySummaryAsync(string project, string summary, ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
        => analysisStore.UpsertRepositorySummaryAsync(project, summary, confidence, sourceHash, modelUsed);

    public Task<ProjectSummary?> GetRepositorySummaryAsync(string project)
        => analysisStore.GetRepositorySummaryAsync(project);

    public Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
        => analysisStore.UpsertProjectAnalysisAsync(repo, analysis);

    public Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
        => analysisStore.GetProjectAnalysesAsync(repo);

    public Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
        => analysisStore.CreateAnalysisBatchAsync(batch);

    public Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
        => analysisStore.CreateBatchRequestsAsync(requests);

    public Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null)
        => analysisStore.GetPendingBatchesAsync(repo);

    public Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
        => analysisStore.GetLatestBatchAsync(repo);

    public Task<StoredAnalysisBatch?> GetBatchByProviderBatchIdAsync(string providerBatchId)
        => analysisStore.GetBatchByProviderBatchIdAsync(providerBatchId);

    public Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt)
        => analysisStore.UpdateBatchStatusAsync(batchId, status, completedCount, completedAt);

    public Task UpdateBatchRequestStateAsync(long batchId, string customId, string status, int attemptCount, string? responseText, string? modelUsed, DateTime? completedAt)
        => analysisStore.UpdateBatchRequestStateAsync(batchId, customId, status, attemptCount, responseText, modelUsed, completedAt);

    public Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
        => analysisStore.GetBatchRequestsAsync(batchId);

    public Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
        => analysisStore.UpsertNodeAnalysisAsync(analysis);

    public Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
        => analysisStore.GetNodeAnalysisAsync(nodeId);

    public Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
        => analysisStore.GetNodeAnalysesBatchAsync(nodeIds);

    public Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project)
        => analysisStore.GetClassNodesWithEdgesAsync(project);

    public Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId)
        => analysisStore.GetChildNodesAsync(parentNodeId);

    public Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId)
        => analysisStore.GetOutboundEdgesAsync(nodeId);

    public Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
        => analysisStore.GetInboundEdgesAsync(nodeId);

    public Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project)
        => analysisStore.GetAllNodesByProjectAsync(project);

    public Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project)
        => analysisStore.GetAllEdgesByProjectAsync(project);

    public Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds)
        => analysisStore.GetEdgesForNodesAsync(nodeIds);

    public Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics)
        => metricsStore.UpsertFileMetricsBatchAsync(project, metrics);

    public Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string project, string? dotnetProject = null)
        => metricsStore.GetFileMetricsAsync(project, dotnetProject);

    public Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10)
        => metricsStore.GetHotspotsAsync(project, top);

    public Task DeleteFileMetricsAsync(string project)
        => metricsStore.DeleteFileMetricsAsync(project);

    public Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
        => metricsStore.UpsertProjectHealthSummaryAsync(summary);

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project)
        => metricsStore.GetProjectHealthSummariesAsync(project);

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync()
        => metricsStore.GetAllRepoHealthSummariesAsync();

    public Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
        => metricsStore.UpsertProjectHealthAnalysisAsync(analysis);

    public Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project)
        => metricsStore.GetProjectHealthAnalysesAsync(project);

    public Task DeleteSecurityFindingsAsync(string project)
        => metricsStore.DeleteSecurityFindingsAsync(project);

    public Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings)
        => metricsStore.UpsertSecurityFindingsBatchAsync(project, findings);

    public Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project)
        => metricsStore.GetSecurityFindingsAsync(project);

    public Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary)
        => metricsStore.UpsertProjectSecuritySummaryAsync(summary);

    public Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project)
        => metricsStore.GetProjectSecuritySummaryAsync(project);

    public Task DeleteProjectDiagnosticsAsync(string project)
        => reviewStore.DeleteProjectDiagnosticsAsync(project);

    public Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
        => reviewStore.UpsertProjectDiagnosticsBatchAsync(project, diagnostics);

    public Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string project, string? dotnetProject = null)
        => reviewStore.GetProjectDiagnosticsAsync(project, dotnetProject);

    public Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run)
        => reviewStore.CreateProjectReviewRunAsync(run);

    public Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null, DateTime? completedAt = null, string? error = null)
        => reviewStore.UpdateProjectReviewRunStatusAsync(reviewRunId, status, overviewJson, completedAt, error);

    public Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings)
        => reviewStore.UpsertProjectReviewFindingsAsync(reviewRunId, findings);

    public Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId)
        => reviewStore.GetProjectReviewRunAsync(reviewRunId);

    public Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string project, string projectName)
        => reviewStore.GetLatestProjectReviewRunAsync(project, projectName);

    public Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId)
        => reviewStore.GetProjectReviewFindingsAsync(reviewRunId);

    public Task<long> CreateRepositoryReviewRunAsync(RepositoryReviewRunEntity run)
        => reviewStore.CreateRepositoryReviewRunAsync(run);

    public Task UpdateRepositoryReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null, DateTime? completedAt = null, string? error = null)
        => reviewStore.UpdateRepositoryReviewRunStatusAsync(reviewRunId, status, overviewJson, completedAt, error);

    public Task UpsertRepositoryReviewFindingsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewFindingEntity> findings)
        => reviewStore.UpsertRepositoryReviewFindingsAsync(reviewRunId, findings);

    public Task UpsertRepositoryReviewProjectSectionsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewProjectSectionEntity> sections)
        => reviewStore.UpsertRepositoryReviewProjectSectionsAsync(reviewRunId, sections);

    public Task<RepositoryReviewRunEntity?> GetRepositoryReviewRunAsync(long reviewRunId)
        => reviewStore.GetRepositoryReviewRunAsync(reviewRunId);

    public Task<RepositoryReviewRunEntity?> GetLatestRepositoryReviewRunAsync(string repo)
        => reviewStore.GetLatestRepositoryReviewRunAsync(repo);

    public Task<IReadOnlyList<RepositoryReviewRunEntity>> GetRepositoryReviewRunsByStatusAsync(IReadOnlyList<string> statuses)
        => reviewStore.GetRepositoryReviewRunsByStatusAsync(statuses);

    public Task<IReadOnlyList<RepositoryReviewFindingEntity>> GetRepositoryReviewFindingsAsync(long reviewRunId)
        => reviewStore.GetRepositoryReviewFindingsAsync(reviewRunId);

    public Task<IReadOnlyList<RepositoryReviewProjectSectionEntity>> GetRepositoryReviewProjectSectionsAsync(long reviewRunId)
        => reviewStore.GetRepositoryReviewProjectSectionsAsync(reviewRunId);

    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task WithDeadlockRetryAsync(Func<Task> action, int maxRetries = 3)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (MySqlException ex) when (ex.Number == 1213 && attempt < maxRetries)
            {
                var delay = (attempt + 1) * 200 + Random.Shared.Next(100);
                logger.LogWarning("MariaDB deadlock on attempt {Attempt}; retrying in {Delay}ms", attempt + 1, delay);
                await Task.Delay(delay);
            }
        }
    }

    private static IQueryable<NodeEntity> ApplyNodeSearchFilters(
        IQueryable<NodeEntity> query,
        string? project,
        NodeLabel? label,
        string? filePattern,
        string? dotnetProject)
    {
        if (project is not null)
        {
            query = query.Where(n => n.Project == project);
        }

        if (label is not null)
        {
            var labelName = label.Value.ToString();
            query = query.Where(n => n.Label == labelName);
        }

        if (filePattern is not null)
        {
            query = query.Where(n => EF.Functions.Like(n.FilePath, $"%{filePattern}%"));
        }

        if (dotnetProject is not null)
        {
            query = query.Where(n => n.DotnetProject == dotnetProject);
        }

        return query;
    }

    private static ProjectInfo MapProjectInfo(RepositoryEntity repository)
        => new(
            repository.Name,
            repository.RepoUrl,
            repository.SourceGroup,
            repository.LocalPath,
            repository.LastCommitSha,
            repository.IndexedAt,
            repository.Language,
            repository.Framework,
            repository.IsFoundational,
            DeserializeJson(repository.Properties));

    private static GraphNode MapNode(NodeEntity node)
        => new()
        {
            Id = node.Id,
            Project = node.Project,
            DotnetProject = node.DotnetProject,
            Label = Enum.Parse<NodeLabel>(node.Label),
            Name = node.Name,
            QualifiedName = node.QualifiedName,
            FilePath = node.FilePath,
            StartLine = node.StartLine,
            EndLine = node.EndLine,
            Properties = DeserializeJson(node.Properties) ?? new(),
            DoNotTrust = node.DoNotTrust
        };

    private static GraphNode MapNode(TraversalRow row)
        => new()
        {
            Id = row.Id,
            Project = row.Project,
            DotnetProject = row.DotnetProject,
            Label = Enum.Parse<NodeLabel>(row.Label),
            Name = row.Name,
            QualifiedName = row.QualifiedName,
            FilePath = row.FilePath ?? "",
            StartLine = row.StartLine,
            EndLine = row.EndLine,
            Properties = DeserializeJson(row.Properties) ?? new(),
            DoNotTrust = row.DoNotTrust
        };

    private static GraphEdge MapEdge(EdgeEntity edge)
        => new()
        {
            Id = edge.Id,
            Project = edge.Project,
            SourceId = edge.SourceId,
            TargetId = edge.TargetId,
            Type = Enum.Parse<EdgeType>(edge.Type),
            Properties = DeserializeJson(edge.Properties) ?? new()
        };

    private static CrossRepoEdge MapCrossRepoEdge(CrossRepoEdgeEntity edge)
        => new()
        {
            Id = edge.Id,
            SourceProject = edge.SourceProject,
            TargetProject = edge.TargetProject,
            SourceNodeId = edge.SourceNodeId,
            TargetNodeId = edge.TargetNodeId,
            Type = Enum.Parse<EdgeType>(edge.Type),
            Properties = DeserializeJson(edge.Properties) ?? new()
        };

    private static RepoCluster MapRepoCluster(RepoClusterRow row)
        => new()
        {
            Id = row.Id,
            ProjectName = row.ProjectName,
            ClusterId = row.ClusterId,
            ClusterLabel = row.ClusterLabel,
            ModularityScore = row.ModularityScore,
            Level = row.Level,
            BetweennessCentrality = row.BetweennessCentrality,
            ComputedAt = row.ComputedAt
        };

    private static string? SerializeJson(Dictionary<string, object>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values, JsonOptions) : null;

    private static Dictionary<string, object>? DeserializeJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed record TraversalRow(
        long Id,
        string Project,
        string? DotnetProject,
        string Label,
        string Name,
        string QualifiedName,
        string? FilePath,
        int StartLine,
        int EndLine,
        string? Properties,
        bool DoNotTrust,
        int Depth,
        string EdgeType,
        long? ParentNodeId,
        string? EdgeProperties);

    private sealed record RepoClusterRow(
        long Id,
        string ProjectName,
        int ClusterId,
        string? ClusterLabel,
        decimal ModularityScore,
        int Level,
        decimal BetweennessCentrality,
        DateTime ComputedAt);
}
