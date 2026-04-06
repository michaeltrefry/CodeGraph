using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── Nodes — simple queries via EF, batch upsert via Dapper ────────────

    public async Task<long> UpsertNodeAsync(GraphNode node)
    {
        var nodeName = node.Name.Length > 1000 ? node.Name[..1000] : node.Name;
        var nodeQualifiedName = node.QualifiedName.Length > 1000 ? node.QualifiedName[..1000] : node.QualifiedName;
        if (nodeName.Length != node.Name.Length || nodeQualifiedName.Length != node.QualifiedName.Length)
            logger.LogWarning("Node name/qualifiedName truncated to 1000 chars: {QualifiedName}", node.QualifiedName[..Math.Min(node.QualifiedName.Length, 120)]);
        await using var conn = await GetOpenConnectionAsync();
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
                Name = nodeName,
                QualifiedName = nodeQualifiedName,
                node.FilePath,
                node.StartLine,
                node.EndLine,
                Properties = SerializeJson(node.Properties)
            });
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default)
    {
        if (nodes.Count == 0)
            return new Dictionary<string, long>();

        var result = new Dictionary<string, long>(nodes.Count);
        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in Chunk(nodes, options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO nodes (project, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@Project{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QN{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Props{i})");

                var n = batch[i];
                parameters.Add($"Project{i}", n.Project);
                parameters.Add($"DotnetProject{i}", n.DotnetProject);
                parameters.Add($"Label{i}", n.Label.ToString());
                parameters.Add($"Name{i}", n.Name.Length > 1000 ? n.Name[..1000] : n.Name);
                parameters.Add($"QN{i}", n.QualifiedName.Length > 1000 ? n.QualifiedName[..1000] : n.QualifiedName);
                parameters.Add($"FilePath{i}", n.FilePath);
                parameters.Add($"StartLine{i}", n.StartLine);
                parameters.Add($"EndLine{i}", n.EndLine);
                parameters.Add($"Props{i}", SerializeJson(n.Properties));
            }

            sb.AppendLine("""
                ON DUPLICATE KEY UPDATE
                    name = VALUES(name),
                    label = VALUES(label),
                    dotnet_project = VALUES(dotnet_project),
                    file_path = VALUES(file_path),
                    start_line = VALUES(start_line),
                    end_line = VALUES(end_line),
                    properties = VALUES(properties)
                """);

            var sql = sb.ToString();
            await WithDeadlockRetryAsync(async () => await conn.ExecuteAsync(sql, parameters));

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

    public async Task<GraphNode?> FindNodeByIdAsync(long id)
    {
        var entity = await context.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        return entity is null ? null : MapNodeEntity(entity);
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
    {
        var entity = await context.Nodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Project == project && n.QualifiedName == qualifiedName);
        return entity is null ? null : MapNodeEntity(entity);
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000)
    {
        return await context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.Name == name)
            .Take(limit)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000)
    {
        var labelStr = label.ToString();
        return await context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.Label == labelStr)
            .Take(limit)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000)
    {
        return await context.Nodes
            .AsNoTracking()
            .Where(n => n.Project == project && n.FilePath == filePath)
            .Take(limit)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0, string? dotnetProject = null)
    {
        // Dynamic search with optional filters — Dapper is cleaner here
        await using var conn = await GetOpenConnectionAsync();
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
        if (dotnetProject is not null)
        {
            sb.Append(" AND dotnet_project = @DotnetProject");
            parameters.Add("DotnetProject", dotnetProject);
        }

        sb.Append(" ORDER BY name LIMIT @Limit OFFSET @Offset");
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<dynamic>(sb.ToString(), parameters);
        return rows.Select(MapNodeDynamic).ToList();
    }

    public async Task<int> SearchNodesCountAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null)
    {
        await using var conn = await GetOpenConnectionAsync();
        var sb = new StringBuilder("SELECT COUNT(*) FROM nodes WHERE name LIKE CONCAT('%', @Pattern, '%')");
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
        if (dotnetProject is not null)
        {
            sb.Append(" AND dotnet_project = @DotnetProject");
            parameters.Add("DotnetProject", dotnetProject);
        }

        return await conn.ExecuteScalarAsync<int>(sb.ToString(), parameters);
    }

    public async Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE nodes SET do_not_trust = @doNotTrust WHERE id = @nodeId",
            new { nodeId, doNotTrust });
    }

    public async Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000)
    {
        var labelStr = label.ToString();
        return await context.Nodes
            .AsNoTracking()
            .Where(n => n.Label == labelStr)
            .Take(limit)
            .Select(n => MapNodeEntity(n))
            .ToListAsync();
    }

    public async Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync()
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<(string label, int count)>(
            "SELECT label, COUNT(*) AS count FROM nodes GROUP BY label");
        return rows
            .Where(r => Enum.TryParse<NodeLabel>(r.label, out _))
            .ToDictionary(r => Enum.Parse<NodeLabel>(r.label), r => r.count);
    }

    public async Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return new Dictionary<long, GraphNode>();

        var result = new Dictionary<long, GraphNode>(ids.Count);
        await using var conn = await GetOpenConnectionAsync();

        foreach (var chunk in Chunk(ids, 1000))
        {
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM nodes WHERE id IN @Ids", new { Ids = chunk });
            foreach (var row in rows)
            {
                var node = MapNodeDynamic(row);
                result[node.Id] = node;
            }
        }

        return result;
    }

    public async Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<(string dotnet_project, string label, int count)>(
            "SELECT dotnet_project, label, COUNT(*) AS count FROM nodes WHERE project = @Project AND dotnet_project IS NOT NULL GROUP BY dotnet_project, label",
            new { Project = project });

        var result = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (dotnetProject, label, count) in rows)
        {
            if (!result.TryGetValue(dotnetProject, out var labelCounts))
            {
                labelCounts = new Dictionary<string, int>();
                result[dotnetProject] = labelCounts;
            }
            labelCounts[label] = count;
        }
        return result;
    }

    public async Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<(string label, int count)>(
            "SELECT label, COUNT(*) AS count FROM nodes WHERE project = @Project GROUP BY label",
            new { Project = project });
        return rows.ToDictionary(r => r.label, r => r.count);
    }
}
