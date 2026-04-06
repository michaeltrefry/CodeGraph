using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore(
    Neo4jSessionFactory sessionFactory,
    CodeGraphStorageOptions options,
    ILogger<Neo4jGraphStore> logger)
    : IGraphStore, IExclusionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Repositories ──────────────────────────────────────────────────────

    public async Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        await using var session = sessionFactory.GetSession();
        var now = DateTime.UtcNow;

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (r:Repository {name: $name})
                ON CREATE SET r.createdAt = $now
                SET r.repoUrl = $repoUrl,
                    r.gitLabGroup = $gitLabGroup,
                    r.localPath = $localPath,
                    r.defaultBranch = $defaultBranch,
                    r.lastCommitSha = COALESCE($lastCommitSha, r.lastCommitSha),
                    r.indexedAt = $now,
                    r.language = COALESCE($language, r.language),
                    r.framework = COALESCE($framework, r.framework),
                    r.isFoundational = $isFoundational,
                    r.properties = $properties,
                    r.updatedAt = $now
                """,
                new
                {
                    name = repository.Name,
                    now,
                    repoUrl = repository.RepoUrl,
                    gitLabGroup = repository.GitLabGroup,
                    localPath = repository.LocalPath,
                    defaultBranch = repository.DefaultBranch,
                    lastCommitSha = repository.LastCommitSha,
                    language = repository.Language,
                    framework = repository.Framework,
                    isFoundational = repository.IsFoundational,
                    properties = repository.Properties
                });
        });
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (r:Repository) RETURN r ORDER BY r.name");
            var results = new List<ProjectInfo>();
            await foreach (var record in cursor)
            {
                var node = record["r"].As<INode>();
                results.Add(MapRepositoryNode(node));
            }
            return results;
        });
    }

    public async Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var conditions = new List<string>();
            var parameters = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(group))
            {
                conditions.Add("r.gitLabGroup = $group");
                parameters["group"] = group;
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("toLower(r.name) CONTAINS toLower($search)");
                parameters["search"] = search;
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var countCursor = await tx.RunAsync(
                $"MATCH (r:Repository) {whereClause} RETURN count(r) AS total", parameters);
            await countCursor.FetchAsync();
            var total = countCursor.Current["total"].As<int>();

            parameters["skip"] = (page - 1) * pageSize;
            parameters["limit"] = pageSize;

            var cursor = await tx.RunAsync(
                $"MATCH (r:Repository) {whereClause} RETURN r ORDER BY r.name SKIP $skip LIMIT $limit",
                parameters);
            var items = new List<ProjectInfo>();
            await foreach (var record in cursor)
                items.Add(MapRepositoryNode(record["r"].As<INode>()));

            return new RepositorySearchResult(items, total);
        });
    }

    public async Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (r:Repository) WHERE r.gitLabGroup IS NOT NULL AND r.gitLabGroup <> '' " +
                "RETURN DISTINCT r.gitLabGroup AS grp ORDER BY grp");
            var results = new List<string>();
            await foreach (var record in cursor)
                results.Add(record["grp"].As<string>());
            return results;
        });
    }

    public async Task<ProjectInfo?> GetRepositoryByName(string name)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (r:Repository {name: $name}) RETURN r",
                new { name });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["r"].As<INode>();
                return MapRepositoryNode(node);
            }
            return null;
        });
    }

    public async Task DeleteRepositoryAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (r:Repository {name: $name}) DETACH DELETE r",
                new { name = project });
        });
    }

    // ── Sync State ────────────────────────────────────────────────────────

    public async Task<SyncStateEntity?> GetSyncStateAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:SyncState {project: $project}) RETURN s",
                new { project });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["s"].As<INode>();
                return new SyncStateEntity
                {
                    Project = node["project"].As<string>(),
                    LastSyncAt = GetDateTimeOrNull(node, "lastSyncAt"),
                    LastCommitSha = GetStringOrNull(node, "lastCommitSha"),
                    Status = node["status"].As<string>(),
                    ErrorMessage = GetStringOrNull(node, "errorMessage")
                };
            }
            return null;
        });
    }

    public async Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (s:SyncState {project: $project})
                SET s.lastSyncAt = $lastSyncAt,
                    s.lastCommitSha = COALESCE($lastCommitSha, s.lastCommitSha),
                    s.status = $status,
                    s.errorMessage = $errorMessage
                """,
                new
                {
                    project = state.Project,
                    lastSyncAt = state.LastSyncAt,
                    lastCommitSha = state.LastCommitSha,
                    status = state.Status,
                    errorMessage = state.ErrorMessage
                });
        });
    }

    public async Task DeleteSyncStateAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (s:SyncState {project: $project}) DELETE s",
                new { project });
        });
    }

    // ── Project Cleanup ───────────────────────────────────────────────────

    public async Task DeleteAllEdgesForProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete all relationships between CodeNodes in this project
            await tx.RunAsync("""
                MATCH (n:CodeNode {project: $project})-[r]-()
                DELETE r
                """,
                new { project });
        });
    }

    public async Task DeleteCrossRepoEdgesForProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (cre:CrossRepoEdge)
                WHERE cre.sourceProject = $project OR cre.targetProject = $project
                DELETE cre
                """,
                new { project });
        });
    }

    public async Task DeleteAnalysisDataForProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete batch requests and batches together
            await tx.RunAsync("""
                MATCH (b:AnalysisBatch {repo: $project})
                OPTIONAL MATCH (b)-[:HAS_REQUEST]->(br:AnalysisBatchRequest)
                DETACH DELETE br, b
                """, new { project });

            // Delete standalone analysis nodes (project analyses, summaries, health)
            await tx.RunAsync("""
                OPTIONAL MATCH (a:ProjectAnalysis {repo: $project})
                OPTIONAL MATCH (s:RepositorySummary {project: $project})
                OPTIONAL MATCH (ha:ProjectHealthAnalysis {project: $project})
                OPTIONAL MATCH (hs:ProjectHealthSummary {project: $project})
                DELETE a, s, ha, hs
                """, new { project });

            // Delete node analyses for nodes in this project
            await tx.RunAsync("""
                MATCH (n:CodeNode {project: $project})-[:HAS_ANALYSIS]->(na:NodeAnalysis)
                DELETE na
                """, new { project });
        });
    }

    // ── Shared Helpers ────────────────────────────────────────────────────

    private static ProjectInfo MapRepositoryNode(INode node) => new(
        node["name"].As<string>(),
        GetStringOrNull(node, "repoUrl"),
        GetStringOrNull(node, "gitLabGroup"),
        GetStringOrNull(node, "localPath"),
        GetStringOrNull(node, "lastCommitSha"),
        GetDateTimeOrNull(node, "indexedAt"),
        GetStringOrNull(node, "language"),
        GetStringOrNull(node, "framework"),
        node.Properties.ContainsKey("isFoundational") && node["isFoundational"].As<bool>(),
        DeserializeJson(GetStringOrNull(node, "properties")));

    internal static GraphNode MapCodeNode(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Project = node["project"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        Label = Enum.Parse<NodeLabel>(node["label"].As<string>()),
        Name = node["name"].As<string>(),
        QualifiedName = node["qualifiedName"].As<string>(),
        FilePath = GetStringOrNull(node, "filePath") ?? "",
        StartLine = node.Properties.ContainsKey("startLine") ? node["startLine"].As<int>() : 0,
        EndLine = node.Properties.ContainsKey("endLine") ? node["endLine"].As<int>() : 0,
        Properties = DeserializeJson(GetStringOrNull(node, "properties")) ?? new(),
        DoNotTrust = node.Properties.ContainsKey("doNotTrust") && node["doNotTrust"].As<bool>()
    };

    internal static GraphEdge MapEdgeRecord(IRecord record) => new()
    {
        Id = record["id"].As<long>(),
        Project = record["project"].As<string>(),
        SourceId = record["sourceId"].As<long>(),
        TargetId = record["targetId"].As<long>(),
        Type = Enum.Parse<EdgeType>(record["type"].As<string>()),
        Properties = DeserializeJson(record["properties"].As<string?>()) ?? new()
    };

    internal static string? GetStringOrNull(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<string>()
            : null;

    internal static DateTime? GetDateTimeOrNull(INode node, string key)
    {
        if (!node.Properties.TryGetValue(key, out var val) || val is null)
            return null;
        if (val is LocalDateTime ldt) return ldt.ToDateTime();
        if (val is ZonedDateTime zdt) return zdt.ToDateTimeOffset().UtcDateTime;
        if (val is string s && DateTime.TryParse(s, out var dt)) return dt;
        return null;
    }

    internal static string? SerializeJson(Dictionary<string, object>? props)
    {
        if (props is null || props.Count == 0) return null;
        return JsonSerializer.Serialize(props, JsonOptions);
    }

    internal static Dictionary<string, object>? DeserializeJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
    }

    internal static T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    internal static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (int i = 0; i < source.Count; i += chunkSize)
        {
            var end = Math.Min(i + chunkSize, source.Count);
            var chunk = new List<T>(end - i);
            for (int j = i; j < end; j++)
                chunk.Add(source[j]);
            chunks.Add(chunk);
        }
        return chunks;
    }
}
