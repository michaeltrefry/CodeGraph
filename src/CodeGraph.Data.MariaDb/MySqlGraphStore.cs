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

public partial class MySqlGraphStore(
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

    public async Task EnsureRepositoryAsync(RepositoryEntity repository)
    {
        var existing = await db.Repositories.FindAsync(repository.Name);
        if (existing is null)
        {
            await UpsertRepositoryAsync(repository);
            return;
        }

        existing.RepoUrl = repository.RepoUrl ?? existing.RepoUrl;
        existing.SourceGroup = repository.SourceGroup ?? existing.SourceGroup;
        existing.LocalPath = repository.LocalPath ?? existing.LocalPath;
        existing.IsFoundational = repository.IsFoundational;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync()
    {
        var repositories = await db.Repositories.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();

        return repositories.Select(MapRepositoryInfo).ToList();
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
            query = query.Where(p => EF.Functions.Like(p.Name, BuildRepositorySearchPattern(search)));
        }

        var total = await query.CountAsync();
        var repositories = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new RepositorySearchResult(repositories.Select(MapRepositoryInfo).ToList(), total);
    }

    private static string BuildRepositorySearchPattern(string search)
    {
        var pattern = search.Trim().Replace('*', '%');
        return pattern.Contains('%') ? pattern : $"%{pattern}%";
    }

    public async Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
        => await db.Repositories.AsNoTracking()
            .Where(p => p.SourceGroup != null && p.SourceGroup != "")
            .Select(p => p.SourceGroup!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();

    public async Task<RepositoryInfo?> GetRepositoryByName(string name)
    {
        var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name);
        return repository is null ? null : MapRepositoryInfo(repository);
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

    public async Task DeleteRepositoryAsync(string repository)
    {
        var entity = await db.Repositories.FindAsync(repository);
        if (entity is null)
        {
            return;
        }

        db.Repositories.Remove(entity);
        await db.SaveChangesAsync();
    }

    public async Task<long> UpsertNodeAsync(GraphNode node)
    {
        var ids = await UpsertNodeBatchAsync([node]);
        return ids.TryGetValue(GraphNodeKey.Create(node.Repository, Truncate(node.QualifiedName, 1000)), out var id)
            ? id
            : 0;
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken ct = default)
    {
        if (nodes.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        var result = new Dictionary<string, long>(nodes.Count, StringComparer.Ordinal);
        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in nodes.Chunk(options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var sql = new StringBuilder("""
                INSERT INTO nodes (repository, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties, do_not_trust)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Repository{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i}, @DoNotTrust{i})");
                var node = batch[i];
                parameters.Add($"Repository{i}", node.Repository);
                parameters.Add($"DotnetProject{i}", node.DotnetProject);
                parameters.Add($"Label{i}", node.Label.ToString());
                parameters.Add($"Name{i}", Truncate(node.Name, 1000));
                parameters.Add($"QualifiedName{i}", Truncate(node.QualifiedName, 1000));
                parameters.Add($"FilePath{i}", node.FilePath ?? "");
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

            var repositories = batch.Select(n => n.Repository).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var qualifiedNames = batch.Select(n => Truncate(n.QualifiedName, 1000)).Distinct(StringComparer.Ordinal).ToList();
            var lookup = new DynamicParameters(new { Repositories = repositories });
            var rows = await conn.QueryAsync<(long id, string repository, string qualified_name)>(
                $"SELECT id, repository, qualified_name FROM nodes WHERE repository IN @Repositories AND {QualifiedNameMatch(qualifiedNames, lookup)}",
                lookup);

            foreach (var row in rows)
            {
                result[GraphNodeKey.Create(row.repository, row.qualified_name)] = row.id;
            }
        }

        return result;
    }

    public async Task<GraphNode?> FindNodeByIdAsync(long id)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        return node is null ? null : MapNode(node);
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string repository, string qualifiedName)
    {
        var truncatedQualifiedName = Truncate(qualifiedName, 1000);
        // Seek uq_node (repository, qualified_name_hash); qualified_name alone is not indexed.
        var node = await db.Nodes
            .FromSqlRaw(
                "SELECT * FROM nodes WHERE repository = {0} AND qualified_name_hash = UNHEX(SHA2({1}, 256))",
                repository,
                truncatedQualifiedName)
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.QualifiedName == truncatedQualifiedName);

        return node is null ? null : MapNode(node);
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByQualifiedNamesAsync(
        string repository,
        IReadOnlyCollection<string> qualifiedNames)
    {
        if (qualifiedNames.Count == 0)
            return [];

        var result = new List<GraphNode>();
        foreach (var batch in qualifiedNames
                     .Select(name => Truncate(name, 1000))
                     .Distinct(StringComparer.Ordinal)
                     .Chunk(500))
        {
            var names = batch.ToList();
            // Seek uq_node (repository, qualified_name_hash); qualified_name alone is not indexed.
            // The SQL holds only positional placeholders; every value is a parameter.
            var sql = "SELECT * FROM nodes WHERE repository = {0} AND qualified_name_hash IN ("
                + string.Join(", ", names.Select((_, i) => $"UNHEX(SHA2({{{i + 1}}}, 256))"))
                + ")";
            var nodes = await db.Nodes
                .FromSqlRaw(sql, [repository, .. names])
                .AsNoTracking()
                .Where(n => names.Contains(n.QualifiedName))
                .OrderBy(n => n.Id)
                .ToListAsync();
            result.AddRange(nodes.Select(MapNode));
        }

        return result;
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string repository, string name, int limit = 1000)
    {
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Repository == repository && n.Name == name)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string repository, NodeLabel label, int limit = 10000)
    {
        var labelName = label.ToString();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Repository == repository && n.Label == labelName)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string repository, string filePath, int limit = 5000)
    {
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Repository == repository && n.FilePath == filePath)
            .OrderBy(n => n.Id)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? repository,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        int limit = 50,
        int offset = 0,
        string? dotnetProject = null)
    {
        var query = db.Nodes.AsNoTracking()
            .Where(n => EF.Functions.Like(n.Name, $"%{namePattern}%"));

        query = ApplyNodeSearchFilters(query, repository, label, filePattern, dotnetProject);

        var nodes = await query
            .OrderBy(n => n.Name)
            .ThenBy(n => n.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return nodes.Select(MapNode).ToList();
    }

    public Task<int> SearchNodesCountAsync(
        string? repository,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        string? dotnetProject = null)
    {
        var query = db.Nodes.AsNoTracking()
            .Where(n => EF.Functions.Like(n.Name, $"%{namePattern}%"));

        return ApplyNodeSearchFilters(query, repository, label, filePattern, dotnetProject).CountAsync();
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

    public async Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string repository)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Repository == repository && n.DotnetProject != null)
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

    public async Task<Dictionary<string, int>> GetNodeCountsByLabelForRepositoryAsync(string repository)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Repository == repository)
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
                INSERT INTO edges (repository, source_id, target_id, type, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Repository{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Properties{i})");
                var edge = batch[i];
                parameters.Add($"Repository{i}", edge.Repository);
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

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceBatchAsync(
        IReadOnlyList<long> sourceIds,
        EdgeType[]? types = null)
    {
        if (sourceIds.Count == 0)
        {
            return [];
        }

        var typeNames = types?.Select(type => type.ToString()).ToList();
        var result = new List<GraphEdge>();
        foreach (var batch in sourceIds.Chunk(1000))
        {
            var batchIds = batch.ToList();
            var query = db.Edges.AsNoTracking().Where(edge => batchIds.Contains(edge.SourceId));
            if (typeNames is { Count: > 0 })
            {
                query = query.Where(edge => typeNames.Contains(edge.Type));
            }

            result.AddRange((await query.OrderBy(edge => edge.Id).ToListAsync()).Select(MapEdge));
        }

        return result;
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
                INSERT INTO cross_repo_edges (source_repository, target_repository, source_node_id, target_node_id, type, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@SourceRepository{i}, @TargetRepository{i}, @SourceNodeId{i}, @TargetNodeId{i}, @Type{i}, @Properties{i})");
                var edge = batch[i];
                parameters.Add($"SourceRepository{i}", edge.SourceRepository);
                parameters.Add($"TargetRepository{i}", edge.TargetRepository);
                parameters.Add($"SourceNodeId{i}", edge.SourceNodeId);
                parameters.Add($"TargetNodeId{i}", edge.TargetNodeId);
                parameters.Add($"Type{i}", edge.Type.ToString());
                parameters.Add($"Properties{i}", SerializeJson(edge.Properties));
            }

            sql.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");
            await WithDeadlockRetryAsync(() => conn.ExecuteAsync(sql.ToString(), parameters));
        }
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string repository, EdgeType? type = null)
    {
        var query = db.CrossRepoEdges.AsNoTracking()
            .Where(e => e.SourceRepository == repository || e.TargetRepository == repository);
        if (type is not null)
        {
            var typeName = type.Value.ToString();
            query = query.Where(e => e.Type == typeName);
        }

        var edges = await query.OrderBy(e => e.Id).ToListAsync();
        return edges.Select(MapCrossRepoEdge).ToList();
    }

    public async Task<IReadOnlyList<string>> FindRepositoriesWithNoCrossRepoEdgesAsync()
    {
        var repositories = await db.Repositories.AsNoTracking()
            .Where(p => !db.CrossRepoEdges.Any(e => e.SourceRepository == p.Name || e.TargetRepository == p.Name))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

        return repositories;
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
                n.repository,
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

    public async Task<IReadOnlyList<NodeInDegree>> GetTopInDegreeNodesAsync(
        string repository,
        IReadOnlyCollection<EdgeType> edgeTypes,
        IReadOnlyCollection<NodeLabel>? targetLabels,
        int minInDegree,
        int limit)
    {
        if (edgeTypes.Count == 0 || limit <= 0)
            return [];

        var types = edgeTypes.Select(type => type.ToString()).Distinct().ToList();
        var edges = db.Edges.AsNoTracking().Where(e => e.Repository == repository && types.Contains(e.Type));
        if (targetLabels is { Count: > 0 })
        {
            var labels = targetLabels.Select(label => label.ToString()).Distinct().ToList();
            edges = edges.Where(e => db.Nodes.Any(n => n.Id == e.TargetId && labels.Contains(n.Label)));
        }

        // One GROUP BY over the (repository, type) index; only the top rows' nodes are loaded.
        var rows = await edges
            .GroupBy(e => e.TargetId)
            .Select(g => new { TargetId = g.Key, Count = g.Count() })
            .Where(r => r.Count >= minInDegree)
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.TargetId)
            .Take(limit)
            .ToListAsync();

        var nodes = await FindNodesByIdBatchAsync(rows.Select(r => r.TargetId).ToList());
        return rows
            .Where(r => nodes.ContainsKey(r.TargetId))
            .Select(r => new NodeInDegree(nodes[r.TargetId], r.Count))
            .ToList();
    }

    public async Task DeleteNodesByFileAsync(string repository, string filePath)
    {
        var nodes = await db.Nodes.Where(n => n.Repository == repository && n.FilePath == filePath).ToListAsync();
        db.Nodes.RemoveRange(nodes);
        await db.SaveChangesAsync();
    }

    public async Task DeleteNodesByRepositoryAsync(string repository)
    {
        var nodes = await db.Nodes.Where(n => n.Repository == repository).ToListAsync();
        db.Nodes.RemoveRange(nodes);
        await db.SaveChangesAsync();
    }

    public async Task<Dictionary<string, string>> GetFileHashesAsync(string repository)
        => await db.FileHashes.AsNoTracking()
            .Where(f => f.Repository == repository)
            .ToDictionaryAsync(f => f.RelPath, f => f.ContentHash, StringComparer.OrdinalIgnoreCase);

    public async Task<Dictionary<string, FileHashRecord>> GetFileHashRecordsAsync(string repository)
        => await db.FileHashes.AsNoTracking()
            .Where(f => f.Repository == repository)
            .ToDictionaryAsync(
                f => f.RelPath,
                f => new FileHashRecord(f.ContentHash, f.ExtractionVersion),
                StringComparer.OrdinalIgnoreCase);

    public async Task UpsertFileHashBatchAsync(
        string repository,
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
                INSERT INTO file_hashes (repository, rel_path, content_hash)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@Repository{i}, @RelPath{i}, @ContentHash{i})");
                parameters.Add($"Repository{i}", repository);
                parameters.Add($"RelPath{i}", batch[i].Key);
                parameters.Add($"ContentHash{i}", batch[i].Value);
            }

            sql.AppendLine("ON DUPLICATE KEY UPDATE content_hash = VALUES(content_hash)");
            await conn.ExecuteAsync(sql.ToString(), parameters);
        }

        // After the write, on its own connection, best effort (sc-27869): knowledge entries waiting
        // for these files' first hash take it as their baseline. Never fails the hash write.
        await KnowledgeBaselines.AdoptWrittenAsync(options.ConnectionString, repository, hashes.ToList(), logger, ct);
    }

    public async Task DeleteFileHashesAsync(string repository, IReadOnlyList<string> relPaths)
    {
        if (relPaths.Count == 0)
        {
            return;
        }

        foreach (var batch in relPaths.Chunk(1000))
        {
            var batchPaths = batch.ToList();
            var hashes = await db.FileHashes
                .Where(f => f.Repository == repository && batchPaths.Contains(f.RelPath))
                .ToListAsync();
            db.FileHashes.RemoveRange(hashes);
        }

        await db.SaveChangesAsync();
    }

    public Task<SyncStateEntity?> GetSyncStateAsync(string repository)
        => db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Repository == repository);

    public async Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> repositories)
    {
        if (repositories.Count == 0)
        {
            return new Dictionary<string, SyncStateEntity>();
        }

        var states = await db.SyncStates.AsNoTracking()
            .Where(s => repositories.Contains(s.Repository))
            .ToListAsync();

        return states.ToDictionary(s => s.Repository, StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        var existing = await db.SyncStates.FindAsync(state.Repository);
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
            existing.FailedCommitSha = state.FailedCommitSha;
            existing.ConsecutiveFailures = state.ConsecutiveFailures;
            existing.LastFailureAt = state.LastFailureAt;
            existing.ExtractionFingerprint = state.ExtractionFingerprint ?? existing.ExtractionFingerprint;
        }

        await db.SaveChangesAsync();
    }

    public async Task SetVitalsVersionAsync(string repository, int vitalsVersion)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sync_state
            SET vitals_version = {vitalsVersion},
                vitals_failed_commit_sha = NULL,
                vitals_failed_version = NULL,
                vitals_consecutive_failures = 0
            WHERE repository = {repository}
            """);
    }

    public async Task<int> RecordVitalsFailureAsync(string repository, string? commitSha, int vitalsVersion)
    {
        // One statement: the count is consecutive only while the commit and version stay the same.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sync_state
            SET vitals_consecutive_failures = CASE
                    WHEN vitals_failed_commit_sha <=> {commitSha} AND vitals_failed_version <=> {vitalsVersion}
                        THEN vitals_consecutive_failures + 1
                    ELSE 1
                END,
                vitals_failed_commit_sha = {commitSha},
                vitals_failed_version = {vitalsVersion}
            WHERE repository = {repository}
            """);
        return await db.SyncStates.AsNoTracking()
            .Where(s => s.Repository == repository)
            .Select(s => s.VitalsConsecutiveFailures)
            .FirstOrDefaultAsync();
    }

    public async Task DeleteSyncStateAsync(string repository)
    {
        var state = await db.SyncStates.FindAsync(repository);
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
                INSERT INTO repo_clusters (repository_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at)
                VALUES
                """);
            var parameters = new DynamicParameters();

            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine($"(@RepositoryName{i}, @ClusterId{i}, @ClusterLabel{i}, @ModularityScore{i}, @Level{i}, @BetweennessCentrality{i}, @ComputedAt{i})");
                var cluster = batch[i];
                parameters.Add($"RepositoryName{i}", cluster.RepositoryName);
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
            SELECT id, repository_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at
            FROM repo_clusters
            WHERE level = @Level
            ORDER BY cluster_id, repository_name
            """, new { Level = level });

        return rows.Select(MapRepoCluster).ToList();
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<RepoClusterRow>("""
            SELECT id, repository_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at
            FROM repo_clusters
            WHERE cluster_id = @ClusterId AND level = @Level
            ORDER BY betweenness_centrality DESC
            """, new { ClusterId = clusterId, Level = level });

        return rows.Select(MapRepoCluster).ToList();
    }

    public async Task DeleteAllEdgesForRepositoryAsync(string repository)
    {
        var edges = await db.Edges.Where(e => e.Repository == repository).ToListAsync();
        db.Edges.RemoveRange(edges);
        await db.SaveChangesAsync();
    }

    public async Task DeleteCrossRepoEdgesForRepositoryAsync(string repository)
    {
        // Delete server-side; tracking every cross-repo edge of a large repository just to remove it is wasteful.
        await db.CrossRepoEdges
            .Where(e => e.SourceRepository == repository || e.TargetRepository == repository)
            .ExecuteDeleteAsync();
    }

    public async Task DeleteAnalysisDataForRepositoryAsync(string repository)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("""
            DELETE br FROM analysis_batch_requests br
            INNER JOIN analysis_batches b ON br.batch_id = b.id
            WHERE b.repo = @Repository
            """, new { Repository = repository });
        await conn.ExecuteAsync("DELETE FROM analysis_batches WHERE repo = @Repository", new { Repository = repository });
        await conn.ExecuteAsync("DELETE FROM project_analyses WHERE repo = @Repository", new { Repository = repository });
        await conn.ExecuteAsync("DELETE FROM repository_summaries WHERE repository = @Repository", new { Repository = repository });
        await conn.ExecuteAsync("""
            DELETE na FROM node_analysis na
            INNER JOIN nodes n ON n.id = na.node_id
            WHERE n.repository = @Repository
            """, new { Repository = repository });
        await conn.ExecuteAsync("DELETE FROM project_health_analyses WHERE repository = @Repository", new { Repository = repository });
        await conn.ExecuteAsync("DELETE FROM project_health_summaries WHERE repository = @Repository", new { Repository = repository });
    }

    public Task UpsertRepositorySummaryAsync(string repository, string summary, ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
        => analysisStore.UpsertRepositorySummaryAsync(repository, summary, confidence, sourceHash, modelUsed);

    public Task<RepositorySummary?> GetRepositorySummaryAsync(string repository)
        => analysisStore.GetRepositorySummaryAsync(repository);

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

    public Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt, string? error = null)
        => analysisStore.UpdateBatchStatusAsync(batchId, status, completedCount, completedAt, error);

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

    public Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string repository)
        => analysisStore.GetClassNodesWithEdgesAsync(repository);

    public Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId)
        => analysisStore.GetChildNodesAsync(parentNodeId);

    public Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId)
        => analysisStore.GetOutboundEdgesAsync(nodeId);

    public Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
        => analysisStore.GetInboundEdgesAsync(nodeId);

    public Task<IReadOnlyList<NodeEntity>> GetAllNodesByRepositoryAsync(string repository)
        => analysisStore.GetAllNodesByRepositoryAsync(repository);

    public Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByRepositoryAsync(string repository)
        => analysisStore.GetAllEdgesByRepositoryAsync(repository);

    public Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds)
        => analysisStore.GetEdgesForNodesAsync(nodeIds);

    public Task<IReadOnlyList<EdgeEntity>> GetEdgeEndpointsForRepositoryAsync(string repository)
        => analysisStore.GetEdgeEndpointsForRepositoryAsync(repository);

    public Task<IReadOnlyList<NodeEntity>> GetFileAndUntrustedNodesAsync(string repository)
        => analysisStore.GetFileAndUntrustedNodesAsync(repository);

    public Task UpsertFileMetricsBatchAsync(string repository, IReadOnlyList<FileMetricsEntity> metrics)
        => metricsStore.UpsertFileMetricsBatchAsync(repository, metrics);

    public Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string repository, string? dotnetProject = null)
        => metricsStore.GetFileMetricsAsync(repository, dotnetProject);

    public Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string repository, int top = 10)
        => metricsStore.GetHotspotsAsync(repository, top);

    public Task<FileMetricsEntity?> GetFileMetricAsync(string repository, string filePath)
        => metricsStore.GetFileMetricAsync(repository, filePath);

    public Task DeleteFileMetricsAsync(string repository)
        => metricsStore.DeleteFileMetricsAsync(repository);

    public Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
        => metricsStore.UpsertProjectHealthSummaryAsync(summary);

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string repository)
        => metricsStore.GetProjectHealthSummariesAsync(repository);

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync()
        => metricsStore.GetAllRepoHealthSummariesAsync();

    public Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
        => metricsStore.UpsertProjectHealthAnalysisAsync(analysis);

    public Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string repository)
        => metricsStore.GetProjectHealthAnalysesAsync(repository);

    public Task DeleteSecurityFindingsAsync(string repository)
        => metricsStore.DeleteSecurityFindingsAsync(repository);

    public Task UpsertSecurityFindingsBatchAsync(string repository, IReadOnlyList<SecurityFindingEntity> findings)
        => metricsStore.UpsertSecurityFindingsBatchAsync(repository, findings);

    public Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string repository)
        => metricsStore.GetSecurityFindingsAsync(repository);

    public Task UpsertRepositorySecuritySummaryAsync(RepositorySecuritySummaryEntity summary)
        => metricsStore.UpsertRepositorySecuritySummaryAsync(summary);

    public Task<RepositorySecuritySummaryEntity?> GetRepositorySecuritySummaryAsync(string repository)
        => metricsStore.GetRepositorySecuritySummaryAsync(repository);

    public Task DeleteProjectDiagnosticsAsync(string repository)
        => reviewStore.DeleteProjectDiagnosticsAsync(repository);

    public Task UpsertProjectDiagnosticsBatchAsync(string repository, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
        => reviewStore.UpsertProjectDiagnosticsBatchAsync(repository, diagnostics);

    public Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string repository, string? dotnetProject = null)
        => reviewStore.GetProjectDiagnosticsAsync(repository, dotnetProject);

    public Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run)
        => reviewStore.CreateProjectReviewRunAsync(run);

    public Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null, DateTime? completedAt = null, string? error = null)
        => reviewStore.UpdateProjectReviewRunStatusAsync(reviewRunId, status, overviewJson, completedAt, error);

    public Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings)
        => reviewStore.UpsertProjectReviewFindingsAsync(reviewRunId, findings);

    public Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId)
        => reviewStore.GetProjectReviewRunAsync(reviewRunId);

    public Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string repository, string repositoryName)
        => reviewStore.GetLatestProjectReviewRunAsync(repository, repositoryName);

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
        for (var attempt = 0; ; attempt++)
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
        string? repository,
        NodeLabel? label,
        string? filePattern,
        string? dotnetProject)
    {
        if (repository is not null)
        {
            query = query.Where(n => n.Repository == repository);
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

    private static RepositoryInfo MapRepositoryInfo(RepositoryEntity repository)
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
            Repository = node.Repository,
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
            Repository = row.Repository,
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
            Repository = edge.Repository,
            SourceId = edge.SourceId,
            TargetId = edge.TargetId,
            Type = Enum.Parse<EdgeType>(edge.Type),
            Properties = DeserializeJson(edge.Properties) ?? new()
        };

    private static CrossRepoEdge MapCrossRepoEdge(CrossRepoEdgeEntity edge)
        => new()
        {
            Id = edge.Id,
            SourceRepository = edge.SourceRepository,
            TargetRepository = edge.TargetRepository,
            SourceNodeId = edge.SourceNodeId,
            TargetNodeId = edge.TargetNodeId,
            Type = Enum.Parse<EdgeType>(edge.Type),
            Properties = DeserializeJson(edge.Properties) ?? new()
        };

    private static RepoCluster MapRepoCluster(RepoClusterRow row)
        => new()
        {
            Id = row.Id,
            RepositoryName = row.RepositoryName,
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

    /// <summary>
    /// Builds an indexed exact-match predicate over <c>nodes.qualified_name</c>. The column has
    /// no index of its own — uniqueness is enforced on <c>(repository, qualified_name_hash)</c>, a
    /// stored SHA-256 of the name (migration 061) — so a bare <c>qualified_name = …</c> or
    /// <c>IN (…)</c> scans every node of the repository. Matching the digest first lets MariaDB
    /// range-scan <c>uq_node</c>; the name comparison keeps the result an exact match.
    /// Each name is bound once, as <c>@Qn{i}</c>.
    /// </summary>
    internal static string QualifiedNameMatch(IReadOnlyList<string> qualifiedNames, DynamicParameters parameters)
    {
        if (qualifiedNames.Count == 0)
            return "1 = 0";

        var hashes = new StringBuilder();
        var names = new StringBuilder();
        for (var i = 0; i < qualifiedNames.Count; i++)
        {
            if (i > 0)
            {
                hashes.Append(", ");
                names.Append(", ");
            }

            var name = $"@Qn{i}";
            hashes.Append("UNHEX(SHA2(").Append(name).Append(", 256))");
            names.Append(name);
            parameters.Add($"Qn{i}", qualifiedNames[i]);
        }

        return $"qualified_name_hash IN ({hashes}) AND qualified_name IN ({names})";
    }

    private sealed record TraversalRow(
        long Id,
        string Repository,
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
        string RepositoryName,
        int ClusterId,
        string? ClusterLabel,
        decimal ModularityScore,
        int Level,
        decimal BetweennessCentrality,
        DateTime ComputedAt);
}
