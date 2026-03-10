using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public class MySqlGraphStore : IGraphStore
{
    private readonly CodeGraphDbContext _context;
    private readonly CodeGraphStorageOptions _options;
    private readonly ILogger<MySqlGraphStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static MySqlGraphStore()
    {
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
    }

    public MySqlGraphStore(
        CodeGraphDbContext context,
        IOptions<CodeGraphStorageOptions> options,
        ILogger<MySqlGraphStore> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    /// Get the underlying connection for Dapper queries.
    private MySqlConnection GetConnection()
        => (MySqlConnection)_context.Database.GetDbConnection();

    /// Ensure connection is open for Dapper operations.
    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var conn = GetConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        return conn;
    }

    // ── Projects (EF Core) ────────────────────────────────────────────────

    public async Task UpsertProjectAsync(string name, string? localPath = null,
        string? repoUrl = null, bool isFoundational = false)
    {
        var existing = await _context.Projects.FindAsync(name);
        if (existing is null)
        {
            _context.Projects.Add(new ProjectEntity
            {
                Name = name,
                LocalPath = localPath,
                RepoUrl = repoUrl,
                IsFoundational = isFoundational,
                IndexedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.LocalPath = localPath ?? existing.LocalPath;
            existing.RepoUrl = repoUrl ?? existing.RepoUrl;
            existing.IsFoundational = isFoundational;
            existing.IndexedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync()
    {
        return await _context.Projects
            .OrderBy(p => p.Name)
            .Select(p => new ProjectInfo(
                p.Name,
                p.RepoUrl,
                p.LocalPath,
                p.LastCommitSha,
                p.IndexedAt,
                p.Language,
                p.Framework,
                p.IsFoundational,
                DeserializeJson(p.Properties)
            ))
            .ToListAsync();
    }

    public async Task DeleteProjectAsync(string project)
    {
        var entity = await _context.Projects.FindAsync(project);
        if (entity is not null)
        {
            _context.Projects.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    // ── Nodes — simple queries via EF, batch upsert via Dapper ────────────

    public async Task<long> UpsertNodeAsync(GraphNode node)
    {
        var conn = await GetOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES (@Project, @Label, @Name, @QualifiedName, @FilePath, @StartLine, @EndLine, @Properties)
            ON DUPLICATE KEY UPDATE
                name = VALUES(name),
                label = VALUES(label),
                file_path = VALUES(file_path),
                start_line = VALUES(start_line),
                end_line = VALUES(end_line),
                properties = VALUES(properties),
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                node.Project,
                Label = node.Label.ToString(),
                node.Name,
                node.QualifiedName,
                node.FilePath,
                node.StartLine,
                node.EndLine,
                Properties = SerializeJson(node.Properties)
            });
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0)
            return new Dictionary<string, long>();

        var result = new Dictionary<string, long>(nodes.Count);
        var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(nodes, _options.BatchSize))
        {
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@Project{i}, @Label{i}, @Name{i}, @QN{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Props{i})");

                var n = batch[i];
                parameters.Add($"Project{i}", n.Project);
                parameters.Add($"Label{i}", n.Label.ToString());
                parameters.Add($"Name{i}", n.Name);
                parameters.Add($"QN{i}", n.QualifiedName);
                parameters.Add($"FilePath{i}", n.FilePath);
                parameters.Add($"StartLine{i}", n.StartLine);
                parameters.Add($"EndLine{i}", n.EndLine);
                parameters.Add($"Props{i}", SerializeJson(n.Properties));
            }

            sb.AppendLine("""
                ON DUPLICATE KEY UPDATE
                    name = VALUES(name),
                    label = VALUES(label),
                    file_path = VALUES(file_path),
                    start_line = VALUES(start_line),
                    end_line = VALUES(end_line),
                    properties = VALUES(properties)
                """);

            await conn.ExecuteAsync(sb.ToString(), parameters);

            // Retrieve IDs for all upserted nodes in this batch
            var qns = batch.Select(n => n.QualifiedName).ToList();
            var projects = batch.Select(n => n.Project).Distinct().ToList();
            var rows = await conn.QueryAsync<(long id, string qualified_name)>(
                "SELECT id, qualified_name FROM nodes WHERE project IN @Projects AND qualified_name IN @QNs",
                new { Projects = projects, QNs = qns });

            foreach (var row in rows)
                result[row.qualified_name] = row.id;
        }

        return result;
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
    {
        var entity = await _context.Nodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Project == project && n.QualifiedName == qualifiedName);
        return entity is null ? null : MapNodeEntity(entity);
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name)
    {
        return await _context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.Name == name)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label)
    {
        var labelStr = label.ToString();
        return await _context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.Label == labelStr)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath)
    {
        return await _context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.FilePath == filePath)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0)
    {
        // Dynamic search with optional filters — Dapper is cleaner here
        var conn = await GetOpenConnectionAsync();
        var sb = new StringBuilder("SELECT * FROM nodes WHERE name LIKE CONCAT('%', @Pattern, '%')");
        var parameters = new DynamicParameters();
        parameters.Add("Pattern", namePattern);

        if (project is not null)
        {
            sb.Append(" AND project = @Project");
            parameters.Add("Project", project);
        }
        if (label is not null)
        {
            sb.Append(" AND label = @Label");
            parameters.Add("Label", label.Value.ToString());
        }
        if (filePattern is not null)
        {
            sb.Append(" AND file_path LIKE CONCAT('%', @FilePattern, '%')");
            parameters.Add("FilePattern", filePattern);
        }

        sb.Append(" ORDER BY name LIMIT @Limit OFFSET @Offset");
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<dynamic>(sb.ToString(), parameters);
        return rows.Select(MapNodeDynamic).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label)
    {
        var labelStr = label.ToString();
        return await _context.Nodes
            .AsNoTracking()
            .Where(n => n.Label == labelStr)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    // ── Edges — simple queries via EF, batch insert via Dapper ────────────

    public async Task InsertEdgeAsync(GraphEdge edge)
    {
        var conn = await GetOpenConnectionAsync();
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

    public async Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges)
    {
        if (edges.Count == 0) return;

        var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(edges, _options.BatchSize))
        {
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

            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = _context.Edges.AsNoTracking().Where(e => e.SourceId == sourceId);
        if (typeStr is not null)
            query = query.Where(e => e.Type == typeStr);
        return await query.Select(e => MapEdgeEntity(e)).ToListAsync();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = _context.Edges.AsNoTracking().Where(e => e.TargetId == targetId);
        if (typeStr is not null)
            query = query.Where(e => e.Type == typeStr);
        return await query.Select(e => MapEdgeEntity(e)).ToListAsync();
    }

    public async Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type)
    {
        var typeStr = type.ToString();
        return await _context.Edges
            .AsNoTracking()
            .Where(e => e.Type == typeStr)
            .Select(e => MapEdgeEntity(e))
            .ToListAsync();
    }

    // ── Cross-Repo Edges (EF for simple, Dapper for batch) ────────────────

    public async Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
    {
        var conn = await GetOpenConnectionAsync();
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

    public async Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges)
    {
        if (edges.Count == 0) return;

        var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(edges, _options.BatchSize))
        {
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

            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null)
    {
        var typeStr = type?.ToString();
        var query = _context.CrossRepoEdges
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

    // ── Traversal (Dapper — recursive CTEs) ───────────────────────────────

    public async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0)
    {
        var conn = await GetOpenConnectionAsync();

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
            SELECT DISTINCT n.id, n.project, n.label, n.name, n.qualified_name,
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
        var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM nodes WHERE project = @Project AND file_path = @FilePath",
            new { Project = project, FilePath = filePath });
    }

    public async Task DeleteNodesByProjectAsync(string project)
    {
        var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM nodes WHERE project = @Project",
            new { Project = project });
    }

    // ── File Hashes (EF Core for reads, Dapper for batch upsert) ──────────

    public async Task<Dictionary<string, string>> GetFileHashesAsync(string project)
    {
        return await _context.FileHashes
            .AsNoTracking()
            .Where(f => f.Project == project)
            .ToDictionaryAsync(f => f.RelPath, f => f.ContentHash);
    }

    public async Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes)
    {
        if (hashes.Count == 0) return;

        var conn = await GetOpenConnectionAsync();

        var items = hashes.ToList();
        foreach (var batch in Chunk(items, _options.BatchSize))
        {
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

        var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM file_hashes WHERE project = @Project AND rel_path IN @RelPaths",
            new { Project = project, RelPaths = relPaths });
    }

    // ── Summaries (EF Core) ───────────────────────────────────────────────

    public async Task UpsertProjectSummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        var existing = await _context.ProjectSummaries.FindAsync(project);
        if (existing is null)
        {
            _context.ProjectSummaries.Add(new ProjectSummaryEntity
            {
                Project = project,
                Summary = summary,
                Confidence = confidence.ToString().ToLowerInvariant(),
                SourceHash = sourceHash,
                ModelUsed = modelUsed,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Summary = summary;
            existing.Confidence = confidence.ToString().ToLowerInvariant();
            existing.SourceHash = sourceHash;
            existing.ModelUsed = modelUsed;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
    }

    public async Task<ProjectSummary?> GetProjectSummaryAsync(string project)
    {
        var entity = await _context.ProjectSummaries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Project == project);

        if (entity is null) return null;

        return new ProjectSummary(
            entity.Project,
            entity.Summary,
            Enum.Parse<ConfidenceLevel>(entity.Confidence, ignoreCase: true),
            entity.SourceHash,
            entity.ModelUsed,
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }

    // ── Migrations (Dapper — raw SQL execution) ───────────────────────────

    public async Task ApplyMigrationsAsync(string migrationsPath)
    {
        // Ensure the database exists before connecting to it
        var builder = new MySqlConnectionStringBuilder(_options.ConnectionString);
        var dbName = builder.Database;
        if (!string.IsNullOrEmpty(dbName))
        {
            builder.Database = "";
            using var adminConn = new MySqlConnection(builder.ConnectionString);
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync(
                $"CREATE DATABASE IF NOT EXISTS `{dbName}`");
        }

        var conn = await GetOpenConnectionAsync();

        // Ensure migration_history table exists
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migration_history (
                id INT AUTO_INCREMENT PRIMARY KEY,
                script_name VARCHAR(255) NOT NULL UNIQUE,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB
            """);

        var applied = (await conn.QueryAsync<string>(
            "SELECT script_name FROM migration_history")).ToHashSet();

        var scripts = Directory.GetFiles(migrationsPath, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        foreach (var script in scripts)
        {
            var scriptName = Path.GetFileName(script);
            if (applied.Contains(scriptName))
            {
                _logger.LogDebug("Migration {Script} already applied, skipping", scriptName);
                continue;
            }

            _logger.LogInformation("Applying migration: {Script}", scriptName);
            var sql = await File.ReadAllTextAsync(script);

            // Split on semicolons for multi-statement scripts
            var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            using var transaction = await conn.BeginTransactionAsync();
            try
            {
                foreach (var statement in statements)
                {
                    await conn.ExecuteAsync(statement, transaction: transaction);
                }

                await conn.ExecuteAsync(
                    "INSERT INTO migration_history (script_name) VALUES (@ScriptName)",
                    new { ScriptName = scriptName },
                    transaction: transaction);

                await transaction.CommitAsync();
                _logger.LogInformation("Migration {Script} applied successfully", scriptName);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static GraphNode MapNodeEntity(NodeEntity n) => new()
    {
        Id = n.Id,
        Project = n.Project,
        Label = Enum.Parse<NodeLabel>(n.Label),
        Name = n.Name,
        QualifiedName = n.QualifiedName,
        FilePath = n.FilePath ?? "",
        StartLine = n.StartLine,
        EndLine = n.EndLine,
        Properties = DeserializeJson(n.Properties) ?? new()
    };

    private static GraphEdge MapEdgeEntity(EdgeEntity e) => new()
    {
        Id = e.Id,
        Project = e.Project,
        SourceId = e.SourceId,
        TargetId = e.TargetId,
        Type = Enum.Parse<EdgeType>(e.Type),
        Properties = DeserializeJson(e.Properties) ?? new()
    };

    private static GraphNode MapNodeDynamic(dynamic r) => new()
    {
        Id = (long)r.id,
        Project = (string)r.project,
        Label = Enum.Parse<NodeLabel>((string)r.label),
        Name = (string)r.name,
        QualifiedName = (string)r.qualified_name,
        FilePath = (string)(r.file_path ?? ""),
        StartLine = (int)r.start_line,
        EndLine = (int)r.end_line,
        Properties = DeserializeJson((string?)r.properties) ?? new()
    };

    private static string? SerializeJson(Dictionary<string, object>? props)
    {
        if (props is null || props.Count == 0) return null;
        return JsonSerializer.Serialize(props, JsonOptions);
    }

    private static Dictionary<string, object>? DeserializeJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
    }

    private static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (int i = 0; i < source.Count; i += chunkSize)
        {
            chunks.Add(source.Skip(i).Take(chunkSize).ToList());
        }
        return chunks;
    }

    private static List<List<T>> Chunk<T>(IEnumerable<T> source, int chunkSize)
    {
        return Chunk(source.ToList(), chunkSize);
    }
}
