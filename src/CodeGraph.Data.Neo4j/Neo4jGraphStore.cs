using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore(
    Neo4jSessionFactory sessionFactory,
    IOptions<CodeGraphStorageOptions> optionsAccessor,
    ILogger<Neo4jGraphStore> logger)
    : IGraphStore, IExclusionStore
{
    private const string RepositoryMetadataLabel = "RepositoryRecord";
    private readonly CodeGraphStorageOptions options = optionsAccessor.Value;
    private static readonly string[] AllCodeNodeSpecificLabels = Enum.GetNames<NodeLabel>();
    private static readonly HashSet<string> PromotedNodePropertyKeys =
    [
        "signature",
        "return_type",
        "is_async",
        "is_entry_point",
        "complexity",
        "http_method",
        "route_template",
        "handler",
        "queue_name",
        "exchange_name",
        "message_name",
        "lifetime",
        "interface",
        "implementation",
        "extractor",
        "confidence_band",
        "inferred"
    ];
    private static readonly HashSet<string> PromotedEdgePropertyKeys =
    [
        "confidence",
        "confidence_band",
        "extractor",
        "inferred",
        "source_repo",
        "target_repo",
        "url_pattern",
        "http_method"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Repositories ──────────────────────────────────────────────────────

    public async Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        await using var session = sessionFactory.GetSession();
        var now = DateTime.UtcNow;
        var schemaMetadata = ResolveSchemaMetadata(repository);

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync($$"""
                MERGE (r:{{RepositoryMetadataLabel}} {name: $name})
                ON CREATE SET r.createdAt = $now
                SET r.repoUrl = COALESCE($repoUrl, r.repoUrl),
                    r.sourceGroup = COALESCE($sourceGroup, r.sourceGroup),
                    r.localPath = COALESCE($localPath, r.localPath),
                    r.defaultBranch = COALESCE($defaultBranch, r.defaultBranch),
                    r.lastCommitSha = COALESCE($lastCommitSha, r.lastCommitSha),
                    r.indexedAt = $now,
                    r.language = COALESCE($language, r.language),
                    r.framework = COALESCE($framework, r.framework),
                    r.isFoundational = $isFoundational,
                    r.properties = $properties,
                    r.isDatabaseSchema = $isDatabaseSchema,
                    r.schemaServerName = $schemaServerName,
                    r.schemaDatabaseName = $schemaDatabaseName,
                    r.updatedAt = $now
                """,
                new
                {
                    name = repository.Name,
                    now,
                    repoUrl = repository.RepoUrl,
                    sourceGroup = repository.SourceGroup,
                    localPath = repository.LocalPath,
                    defaultBranch = repository.DefaultBranch,
                    lastCommitSha = repository.LastCommitSha,
                    language = repository.Language,
                    framework = repository.Framework,
                    isFoundational = repository.IsFoundational,
                    properties = repository.Properties,
                    isDatabaseSchema = schemaMetadata.IsDatabaseSchema,
                    schemaServerName = schemaMetadata.ServerName,
                    schemaDatabaseName = schemaMetadata.DatabaseName
                });
        });
    }

    public async Task EnsureRepositoryAsync(RepositoryEntity repository)
    {
        await using var session = sessionFactory.GetSession();
        var now = DateTime.UtcNow;

        var updated = await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel} {{name: $name}}) RETURN count(r) AS count",
                new { name = repository.Name });
            var exists = (await cursor.SingleAsync())["count"].As<long>() > 0;
            if (!exists)
                return false;

            await tx.RunAsync($$"""
                MATCH (r:{{RepositoryMetadataLabel}} {name: $name})
                SET r.repoUrl = COALESCE($repoUrl, r.repoUrl),
                    r.sourceGroup = COALESCE($sourceGroup, r.sourceGroup),
                    r.localPath = COALESCE($localPath, r.localPath),
                    r.isFoundational = $isFoundational,
                    r.updatedAt = $now
                """,
                new
                {
                    name = repository.Name,
                    now,
                    repoUrl = repository.RepoUrl,
                    sourceGroup = repository.SourceGroup,
                    localPath = repository.LocalPath,
                    isFoundational = repository.IsFoundational
                });
            return true;
        });

        if (!updated)
            await UpsertRepositoryAsync(repository);
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel}) RETURN r ORDER BY r.name");
            var results = new List<RepositoryInfo>();
            await foreach (var record in cursor)
            {
                var node = record["r"].As<INode>();
                results.Add(MapRepositoryNode(node));
            }
            return results;
        });
    }

    public async Task UpdateRepositoryCommitShaAsync(string name, string? commitSha)
    {
        await using var session = sessionFactory.GetSession();
        var now = DateTime.UtcNow;

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync($$"""
                MERGE (r:{{RepositoryMetadataLabel}} {name: $name})
                ON CREATE SET r.createdAt = $now
                SET r.lastCommitSha = $lastCommitSha,
                    r.updatedAt = $now
                """,
                new
                {
                    name,
                    now,
                    lastCommitSha = commitSha
                });
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
                conditions.Add("r.sourceGroup = $group");
                parameters["group"] = group;
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("toLower(r.name) =~ $searchPattern");
                parameters["searchPattern"] = BuildRepositorySearchRegex(search);
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var countCursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel}) {whereClause} RETURN count(r) AS total", parameters);
            await countCursor.FetchAsync();
            var total = countCursor.Current["total"].As<int>();

            parameters["skip"] = (page - 1) * pageSize;
            parameters["limit"] = pageSize;

            var cursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel}) {whereClause} RETURN r ORDER BY r.name SKIP $skip LIMIT $limit",
                parameters);
            var items = new List<RepositoryInfo>();
            await foreach (var record in cursor)
                items.Add(MapRepositoryNode(record["r"].As<INode>()));

            return new RepositorySearchResult(items, total);
        });
    }

    private static string BuildRepositorySearchRegex(string search)
    {
        var trimmed = search.Trim();
        var pattern = Regex.Escape(trimmed)
            .Replace("\\*", ".*")
            .Replace("%", ".*");

        if (!trimmed.Contains('*') && !trimmed.Contains('%'))
            pattern = $".*{pattern}.*";

        return pattern.ToLowerInvariant();
    }

    public async Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel}) WHERE r.sourceGroup IS NOT NULL AND r.sourceGroup <> '' " +
                "RETURN DISTINCT r.sourceGroup AS grp ORDER BY grp");
            var results = new List<string>();
            await foreach (var record in cursor)
                results.Add(record["grp"].As<string>());
            return results;
        });
    }

    public async Task<RepositoryInfo?> GetRepositoryByName(string name)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel} {{name: $name}}) RETURN r",
                new { name });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["r"].As<INode>();
                return MapRepositoryNode(node);
            }
            return null;
        });
    }

    public async Task DeleteRepositoryAsync(string repository)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                $"MATCH (r:{RepositoryMetadataLabel} {{name: $name}}) DETACH DELETE r",
                new { name = repository });
        });
    }

    // ── Sync State ────────────────────────────────────────────────────────

    public async Task<SyncStateEntity?> GetSyncStateAsync(string repository)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:SyncState {repository: $repository}) RETURN s",
                new { repository });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["s"].As<INode>();
                return new SyncStateEntity
                {
                    Repository = node["repository"].As<string>(),
                    LastSyncAt = GetDateTimeOrNull(node, "lastSyncAt"),
                    LastCommitSha = GetStringOrNull(node, "lastCommitSha"),
                    Status = node["status"].As<string>(),
                    ErrorMessage = GetStringOrNull(node, "errorMessage"),
                    ExtractionFingerprint = GetStringOrNull(node, "extractionFingerprint"),
                    VitalsVersion = GetIntOrNull(node, "vitalsVersion"),
                    VitalsFailedCommitSha = GetStringOrNull(node, "vitalsFailedCommitSha"),
                    VitalsFailedVersion = GetIntOrNull(node, "vitalsFailedVersion"),
                    VitalsConsecutiveFailures = GetIntOrNull(node, "vitalsConsecutiveFailures") ?? 0
                };
            }
            return null;
        });
    }

    public async Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> repositories)
    {
        if (repositories.Count == 0)
            return new Dictionary<string, SyncStateEntity>(StringComparer.OrdinalIgnoreCase);

        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:SyncState)
                WHERE s.repository IN $repositories
                RETURN s
                """,
                new { repositories });

            var results = new Dictionary<string, SyncStateEntity>(StringComparer.OrdinalIgnoreCase);
            await foreach (var record in cursor)
            {
                var node = record["s"].As<INode>();
                var entity = new SyncStateEntity
                {
                    Repository = node["repository"].As<string>(),
                    LastSyncAt = GetDateTimeOrNull(node, "lastSyncAt"),
                    LastCommitSha = GetStringOrNull(node, "lastCommitSha"),
                    Status = node["status"].As<string>(),
                    ErrorMessage = GetStringOrNull(node, "errorMessage"),
                    ExtractionFingerprint = GetStringOrNull(node, "extractionFingerprint"),
                    VitalsVersion = GetIntOrNull(node, "vitalsVersion"),
                    VitalsFailedCommitSha = GetStringOrNull(node, "vitalsFailedCommitSha"),
                    VitalsFailedVersion = GetIntOrNull(node, "vitalsFailedVersion"),
                    VitalsConsecutiveFailures = GetIntOrNull(node, "vitalsConsecutiveFailures") ?? 0
                };
                results[entity.Repository] = entity;
            }

            return (IReadOnlyDictionary<string, SyncStateEntity>)results;
        });
    }

    public async Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (s:SyncState {repository: $repository})
                SET s.lastSyncAt = $lastSyncAt,
                    s.lastCommitSha = COALESCE($lastCommitSha, s.lastCommitSha),
                    s.extractionFingerprint = COALESCE($extractionFingerprint, s.extractionFingerprint),
                    s.status = $status,
                    s.errorMessage = $errorMessage
                """,
                new
                {
                    repository = state.Repository,
                    extractionFingerprint = state.ExtractionFingerprint,
                    lastSyncAt = state.LastSyncAt,
                    lastCommitSha = state.LastCommitSha,
                    status = state.Status,
                    errorMessage = state.ErrorMessage
                });
        });
    }

    public async Task SetVitalsVersionAsync(string repository, int vitalsVersion)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (s:SyncState {repository: $repository})
                SET s.vitalsVersion = $vitalsVersion,
                    s.vitalsFailedCommitSha = null,
                    s.vitalsFailedVersion = null,
                    s.vitalsConsecutiveFailures = 0
                """,
                new { repository, vitalsVersion });
        });
    }

    public async Task<int> RecordVitalsFailureAsync(string repository, string? commitSha, int vitalsVersion)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:SyncState {repository: $repository})
                WITH s, coalesce(s.vitalsFailedCommitSha, '') = coalesce($commitSha, '')
                        AND s.vitalsFailedVersion = $vitalsVersion AS same
                SET s.vitalsConsecutiveFailures = CASE WHEN same
                        THEN coalesce(s.vitalsConsecutiveFailures, 0) + 1 ELSE 1 END,
                    s.vitalsFailedCommitSha = $commitSha,
                    s.vitalsFailedVersion = $vitalsVersion
                RETURN s.vitalsConsecutiveFailures AS failures
                """,
                new { repository, commitSha, vitalsVersion });
            return await cursor.FetchAsync() ? cursor.Current["failures"].As<int>() : 0;
        });
    }

    public async Task DeleteSyncStateAsync(string repository)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (s:SyncState {repository: $repository}) DELETE s",
                new { repository });
        });
    }

    // ── Repository Cleanup ───────────────────────────────────────────────────

    public async Task DeleteAllEdgesForRepositoryAsync(string repository)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete all relationships between CodeNodes in this repository
            await tx.RunAsync("""
                MATCH (n:CodeNode {repository: $repository})-[r]-()
                DELETE r
                """,
                new { repository });
        });
    }

    public async Task DeleteCrossRepoEdgesForRepositoryAsync(string repository)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (cre:CrossRepoEdge)
                WHERE cre.sourceRepository = $repository OR cre.targetRepository = $repository
                DELETE cre
                """,
                new { repository });
        });
    }

    public async Task DeleteAnalysisDataForRepositoryAsync(string repository)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete batch requests and batches together
            await tx.RunAsync("""
                MATCH (b:AnalysisBatch {repo: $repository})
                OPTIONAL MATCH (b)-[:HAS_REQUEST]->(br:AnalysisBatchRequest)
                DETACH DELETE br, b
                """, new { repository });

            // Delete standalone analysis nodes (repository analyses, summaries, health)
            await tx.RunAsync("""
                OPTIONAL MATCH (a:ProjectAnalysis {repo: $repository})
                OPTIONAL MATCH (s:RepositorySummary {repository: $repository})
                OPTIONAL MATCH (ha:ProjectHealthAnalysis {repository: $repository})
                OPTIONAL MATCH (hs:ProjectHealthSummary {repository: $repository})
                OPTIONAL MATCH (ps:RepositorySecuritySummary {repository: $repository})
                DELETE a, s, ha, hs, ps
                """, new { repository });

            await tx.RunAsync("""
                MATCH (sf:SecurityFinding {repository: $repository})
                DELETE sf
                """, new { repository });

            await tx.RunAsync("""
                MATCH (pd:ProjectDiagnostic {repository: $repository})
                DELETE pd
                """, new { repository });

            await tx.RunAsync("""
                MATCH (rr:ProjectReviewRun {repository: $repository})
                OPTIONAL MATCH (rr)-[rfRel:HAS_FINDING]->(rf:ProjectReviewFinding)
                DELETE rfRel, rf, rr
                """, new { repository });

            await tx.RunAsync("""
                MATCH (rr:RepositoryReviewRun {repo: $repository})-[rfRel:HAS_FINDING]->(rf:RepositoryReviewFinding)
                DELETE rfRel, rf
                """, new { repository });

            await tx.RunAsync("""
                MATCH (rr:RepositoryReviewRun {repo: $repository})-[rsRel:HAS_PROJECT_SECTION]->(rs:RepositoryReviewProjectSection)
                DELETE rsRel, rs
                """, new { repository });

            await tx.RunAsync("""
                MATCH (rr:RepositoryReviewRun {repo: $repository})
                DELETE rr
                """, new { repository });

            // Delete node analyses for nodes in this repository
            await tx.RunAsync("""
                MATCH (n:CodeNode {repository: $repository})-[:HAS_ANALYSIS]->(na:NodeAnalysis)
                DELETE na
                """, new { repository });
        });
    }

    // ── Shared Helpers ────────────────────────────────────────────────────

    private static RepositoryInfo MapRepositoryNode(INode node) => new(
        node["name"].As<string>(),
        GetStringOrNull(node, "repoUrl"),
        GetStringOrNull(node, "sourceGroup"),
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
        Repository = node["repository"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        Label = GetNodeLabel(node),
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
        Id = GetRelationshipId(record),
        Repository = record["repository"].As<string>(),
        SourceId = record["sourceId"].As<long>(),
        TargetId = record["targetId"].As<long>(),
        Type = Enum.Parse<EdgeType>(record["type"].As<string>()),
        Properties = DeserializeJson(record["properties"].As<string?>()) ?? new()
    };

    internal static string? GetStringOrNull(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<string>()
            : null;

    internal static int? GetIntOrNull(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<int>()
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

    internal static string GetCodeNodeMatchPattern(string alias, NodeLabel? label = null)
        => label is null
            ? $"({alias}:CodeNode)"
            : $"({alias}:CodeNode{GetCodeNodeSetLabels(label.Value)})";

    internal static string GetCodeNodeSetLabels(NodeLabel label)
        => string.Concat(GetCodeNodeLabels(label).Select(static l => $":{l}"));

    internal static Dictionary<string, object> ExtractPromotedNodeProperties(Dictionary<string, object>? props)
        => ExtractPromotedProperties(props, PromotedNodePropertyKeys);

    internal static Dictionary<string, object> ExtractPromotedEdgeProperties(Dictionary<string, object>? props)
        => ExtractPromotedProperties(props, PromotedEdgePropertyKeys);

    private static Dictionary<string, object> ExtractPromotedProperties(
        Dictionary<string, object>? props,
        HashSet<string> allowedKeys)
    {
        var promoted = new Dictionary<string, object>(StringComparer.Ordinal);
        if (props is null || props.Count == 0)
            return promoted;

        foreach (var (key, value) in props)
        {
            if (!allowedKeys.Contains(key))
                continue;

            if (TryConvertNeo4jScalar(value, out var converted))
                promoted[key] = converted;
        }

        return promoted;
    }

    private static bool TryConvertNeo4jScalar(object? value, out object converted)
    {
        switch (value)
        {
            case null:
                converted = "";
                return false;
            case string s:
                converted = s;
                return true;
            case bool b:
                converted = b;
                return true;
            case byte or sbyte or short or ushort or int or uint or long:
                converted = value;
                return true;
            case ulong ul when ul <= long.MaxValue:
                converted = (long)ul;
                return true;
            case float f:
                converted = (double)f;
                return true;
            case double d:
                converted = d;
                return true;
            case decimal m:
                converted = (double)m;
                return true;
            default:
                converted = "";
                return false;
        }
    }

    private static IReadOnlyList<string> GetCodeNodeLabels(NodeLabel label)
    {
        var labels = new List<string>();

        switch (label)
        {
            case NodeLabel.Repository:
            case NodeLabel.DotnetProject:
            case NodeLabel.Namespace:
            case NodeLabel.Folder:
            case NodeLabel.File:
                labels.Add("Structural");
                break;

            case NodeLabel.Class:
            case NodeLabel.Interface:
            case NodeLabel.Enum:
            case NodeLabel.Struct:
            case NodeLabel.Record:
            case NodeLabel.Delegate:
                labels.Add("Type");
                break;

            case NodeLabel.Function:
            case NodeLabel.Method:
            case NodeLabel.Property:
            case NodeLabel.Constructor:
                labels.Add("Member");
                break;

            case NodeLabel.Route:
            case NodeLabel.Service:
            case NodeLabel.Job:
                labels.Add("Integration");
                break;

            case NodeLabel.Event:
            case NodeLabel.Queue:
            case NodeLabel.Exchange:
                labels.Add("Messaging");
                break;

            case NodeLabel.Table:
            case NodeLabel.View:
            case NodeLabel.StoredProcedure:
                labels.Add("Storage");
                break;

            case NodeLabel.Component:
            case NodeLabel.Module:
                labels.Add("Frontend");
                break;

            case NodeLabel.NuGetPackage:
            case NodeLabel.Package:
                labels.Add("Package");
                break;

            case NodeLabel.ExternalSymbol:
                labels.Add("External");
                break;
        }

        labels.Add(label.ToString());
        return labels;
    }

    private static NodeLabel GetNodeLabel(INode node)
    {
        if (node.Properties.TryGetValue("label", out var labelValue) &&
            labelValue is not null &&
            Enum.TryParse<NodeLabel>(labelValue.As<string>(), out var propertyLabel))
        {
            return propertyLabel;
        }

        foreach (var label in AllCodeNodeSpecificLabels)
        {
            if (node.Labels.Contains(label) && Enum.TryParse<NodeLabel>(label, out var inferred))
                return inferred;
        }

        throw new InvalidOperationException(
            $"Unable to infer CodeNode label for node {node.ElementId}.");
    }

    private static long GetRelationshipId(IRecord record)
    {
        if (record.Keys.Contains("id"))
            return record["id"].As<long>();

        if (record.Keys.Contains("elementId"))
            return record["elementId"].As<string>().GetHashCode();

        return 0;
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

    private async Task<long> ReserveSequenceRangeAsync(IAsyncSession session, string sequenceName, int count)
    {
        if (count <= 0)
            return 0;

        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (seq:Sequence {name: $sequenceName})
                ON CREATE SET seq.value = 0
                // Increment before reading: SET takes the write lock, so the value read back
                // is this transaction's. Reading seq.value first loses updates under concurrency.
                SET seq.value = seq.value + $count
                RETURN seq.value - $count AS startId
                """,
                new { sequenceName, count });

            await cursor.FetchAsync();
            return cursor.Current["startId"].As<long>();
        });
    }
}
