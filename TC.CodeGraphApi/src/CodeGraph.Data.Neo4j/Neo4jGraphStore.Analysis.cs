using System.Text.Json;
using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Summaries ─────────────────────────────────────────────────────────

    public async Task UpsertRepositorySummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        var now = DateTime.UtcNow;
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (s:RepositorySummary {project: $project})
                ON CREATE SET s.createdAt = $now
                SET s.summary = $summary,
                    s.confidence = $confidence,
                    s.sourceHash = $sourceHash,
                    s.modelUsed = $modelUsed,
                    s.updatedAt = $now
                """,
                new
                {
                    project,
                    summary,
                    confidence = confidence.ToString().ToLowerInvariant(),
                    sourceHash,
                    modelUsed,
                    now
                });
        });
    }

    public async Task<ProjectSummary?> GetRepositorySummaryAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:RepositorySummary {project: $project}) RETURN s",
                new { project });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["s"].As<INode>();
                return new ProjectSummary(
                    node["project"].As<string>(),
                    node["summary"].As<string>(),
                    Enum.Parse<ConfidenceLevel>(node["confidence"].As<string>(), ignoreCase: true),
                    node["sourceHash"].As<string>(),
                    GetStringOrNull(node, "modelUsed"),
                    GetDateTimeOrNull(node, "createdAt") ?? DateTime.MinValue,
                    GetDateTimeOrNull(node, "updatedAt") ?? DateTime.MinValue);
            }
            return null;
        });
    }

    // ── Per-project Analyses ──────────────────────────────────────────────

    public async Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
    {
        var now = DateTime.UtcNow;
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (a:ProjectAnalysis {repo: $repo, projectName: $projectName})
                ON CREATE SET a.createdAt = $now
                SET a.summary = $summary,
                    a.confidence = $confidence,
                    a.endpoints = $endpoints,
                    a.services = $services,
                    a.externalDependencies = $externalDependencies,
                    a.databaseTables = $databaseTables,
                    a.modelUsed = $modelUsed,
                    a.updatedAt = $now
                """,
                new
                {
                    repo,
                    projectName = analysis.ProjectName,
                    summary = analysis.Summary,
                    confidence = analysis.Confidence.ToString().ToLowerInvariant(),
                    endpoints = JsonSerializer.Serialize(analysis.Endpoints, JsonOptions),
                    services = JsonSerializer.Serialize(analysis.Services, JsonOptions),
                    externalDependencies = JsonSerializer.Serialize(analysis.ExternalDependencies, JsonOptions),
                    databaseTables = JsonSerializer.Serialize(analysis.DatabaseTables, JsonOptions),
                    modelUsed = analysis.ModelUsed,
                    now
                });
        });
    }

    public async Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (a:ProjectAnalysis {repo: $repo}) RETURN a",
                new { repo });
            var results = new List<StoredProjectAnalysis>();
            await foreach (var record in cursor)
            {
                var node = record["a"].As<INode>();
                results.Add(new StoredProjectAnalysis(
                    node["repo"].As<string>(),
                    node["projectName"].As<string>(),
                    node["summary"].As<string>(),
                    Enum.Parse<ConfidenceLevel>(node["confidence"].As<string>(), ignoreCase: true),
                    DeserializeJson<List<StoredEndpoint>>(GetStringOrNull(node, "endpoints")) ?? [],
                    DeserializeJson<List<StoredService>>(GetStringOrNull(node, "services")) ?? [],
                    DeserializeJson<List<string>>(GetStringOrNull(node, "externalDependencies")) ?? [],
                    DeserializeJson<List<string>>(GetStringOrNull(node, "databaseTables")) ?? [],
                    GetStringOrNull(node, "modelUsed"),
                    GetDateTimeOrNull(node, "updatedAt") ?? DateTime.MinValue));
            }
            return results;
        });
    }

    // ── Graph Context for Batch Analysis ──────────────────────────────────

    public async Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (n:CodeNode {project: $project})
                WHERE n.label IN ['Class', 'Interface']
                AND (
                    EXISTS { MATCH (e:EdgeRecord {sourceId: n.appId}) }
                    OR EXISTS { MATCH (e:EdgeRecord {targetId: n.appId}) }
                )
                RETURN n ORDER BY n.name
                """,
                new { project });
            var results = new List<NodeEntity>();
            await foreach (var record in cursor)
                results.Add(MapCodeNodeToEntity(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:EdgeRecord {sourceId: $parentNodeId, type: 'DEFINES'})
                MATCH (n:CodeNode {appId: e.targetId})
                RETURN n ORDER BY n.label, n.name
                """,
                new { parentNodeId });
            var results = new List<NodeEntity>();
            await foreach (var record in cursor)
                results.Add(MapCodeNodeToEntity(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:EdgeRecord {sourceId: $nodeId})
                WHERE NOT e.type IN ['DEFINES', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE']
                RETURN e ORDER BY e.type
                """,
                new { nodeId });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNodeToEntity(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:EdgeRecord {targetId: $nodeId})
                WHERE NOT e.type IN ['DEFINES', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE']
                RETURN e ORDER BY e.type
                """,
                new { nodeId });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNodeToEntity(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project}) RETURN n ORDER BY n.label, n.name",
                new { project });
            var results = new List<NodeEntity>();
            await foreach (var record in cursor)
                results.Add(MapCodeNodeToEntity(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:EdgeRecord {project: $project}) RETURN e",
                new { project });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNodeToEntity(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0) return [];
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:EdgeRecord)
                WHERE e.sourceId IN $nodeIds OR e.targetId IN $nodeIds
                RETURN e
                """,
                new { nodeIds });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNodeToEntity(record["e"].As<INode>()));
            return results;
        });
    }

    // ── Analysis Batch Tracking ───────────────────────────────────────────

    public async Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (seq:Sequence {name: 'analysis_batch_id'})
                ON CREATE SET seq.value = 0
                SET seq.value = seq.value + 1
                WITH seq.value AS newId
                CREATE (b:AnalysisBatch {
                    appId: newId,
                    repo: $repo,
                    anthropicBatchId: $anthropicBatchId,
                    status: $status,
                    requestCount: $requestCount,
                    completedCount: $completedCount,
                    submittedAt: $submittedAt,
                    completedAt: $completedAt
                })
                RETURN b.appId AS id
                """,
                new
                {
                    repo = batch.Repo,
                    anthropicBatchId = batch.AnthropicBatchId,
                    status = batch.Status,
                    requestCount = batch.RequestCount,
                    completedCount = batch.CompletedCount,
                    submittedAt = batch.SubmittedAt,
                    completedAt = (object?)batch.CompletedAt
                });
            await cursor.FetchAsync();
            var id = cursor.Current["id"].As<long>();
            batch.Id = id;
            return id;
        });
    }

    public async Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0) return;

        await using var session = sessionFactory.GetSession();
        var items = requestList.Select(r => new Dictionary<string, object?>
        {
            ["batchId"] = r.BatchId,
            ["customId"] = r.CustomId,
            ["nodeId"] = r.NodeId,
            ["nodeLabel"] = r.NodeLabel,
            ["status"] = r.Status,
            ["completedAt"] = (object?)r.CompletedAt
        }).ToList();

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                UNWIND $items AS r
                CREATE (br:AnalysisBatchRequest {
                    batchId: r.batchId,
                    customId: r.customId,
                    nodeId: r.nodeId,
                    nodeLabel: r.nodeLabel,
                    status: r.status,
                    completedAt: r.completedAt
                })
                """,
                new { items });
        });
    }

    public async Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = repo is null
                ? "MATCH (b:AnalysisBatch {status: 'submitted'}) RETURN b ORDER BY b.submittedAt"
                : "MATCH (b:AnalysisBatch {status: 'submitted', repo: $repo}) RETURN b ORDER BY b.submittedAt";

            var cursor = await tx.RunAsync(cypher, new { repo });
            var results = new List<StoredAnalysisBatch>();
            await foreach (var record in cursor)
            {
                var node = record["b"].As<INode>();
                results.Add(MapAnalysisBatchNode(node));
            }
            return results;
        });
    }

    public async Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (b:AnalysisBatch {repo: $repo}) RETURN b ORDER BY b.submittedAt DESC LIMIT 1",
                new { repo });
            if (await cursor.FetchAsync())
                return MapAnalysisBatchNode(cursor.Current["b"].As<INode>());
            return null;
        });
    }

    public async Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (b:AnalysisBatch {appId: $batchId})
                SET b.status = $status,
                    b.completedCount = $completedCount,
                    b.completedAt = $completedAt
                """,
                new { batchId, status, completedCount, completedAt = (object?)completedAt });
        });
    }

    public async Task UpdateBatchRequestStatusAsync(string customId, string status, DateTime completedAt)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (br:AnalysisBatchRequest {customId: $customId})
                SET br.status = $status, br.completedAt = $completedAt
                """,
                new { customId, status, completedAt });
        });
    }

    public async Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (br:AnalysisBatchRequest {batchId: $batchId}) RETURN br",
                new { batchId });
            var results = new List<AnalysisBatchRequestEntity>();
            await foreach (var record in cursor)
            {
                var node = record["br"].As<INode>();
                results.Add(new AnalysisBatchRequestEntity
                {
                    BatchId = node["batchId"].As<long>(),
                    CustomId = node["customId"].As<string>(),
                    NodeId = node.Properties.ContainsKey("nodeId") ? node["nodeId"].As<long?>() : null,
                    NodeLabel = node["nodeLabel"].As<string>(),
                    Status = node["status"].As<string>(),
                    CompletedAt = GetDateTimeOrNull(node, "completedAt")
                });
            }
            return results;
        });
    }

    // ── Node Analysis Results ─────────────────────────────────────────────

    public async Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
    {
        var now = DateTime.UtcNow;
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (na:NodeAnalysis {nodeId: $nodeId})
                ON CREATE SET na.createdAt = $now
                SET na.description = $description,
                    na.confidence = $confidence,
                    na.modelUsed = $modelUsed,
                    na.updatedAt = $now
                """,
                new
                {
                    nodeId = analysis.NodeId,
                    description = analysis.Description,
                    confidence = analysis.Confidence,
                    modelUsed = analysis.ModelUsed,
                    now
                });
        });
    }

    public async Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (na:NodeAnalysis {nodeId: $nodeId}) RETURN na",
                new { nodeId });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["na"].As<INode>();
                return MapNodeAnalysis(node);
            }
            return null;
        });
    }

    public async Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0) return new Dictionary<long, StoredNodeAnalysis>();

        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var result = new Dictionary<long, StoredNodeAnalysis>(nodeIds.Count);

            foreach (var chunk in Chunk(nodeIds, 1000))
            {
                var cursor = await tx.RunAsync(
                    "MATCH (na:NodeAnalysis) WHERE na.nodeId IN $ids RETURN na",
                    new { ids = chunk });
                await foreach (var record in cursor)
                {
                    var analysis = MapNodeAnalysis(record["na"].As<INode>());
                    result[analysis.NodeId] = analysis;
                }
            }
            return result;
        });
    }

    private static StoredNodeAnalysis MapNodeAnalysis(INode node) => new(
        node["nodeId"].As<long>(),
        node["description"].As<string>(),
        node["confidence"].As<string>(),
        GetStringOrNull(node, "modelUsed"),
        GetDateTimeOrNull(node, "createdAt") ?? DateTime.MinValue,
        GetDateTimeOrNull(node, "updatedAt") ?? DateTime.MinValue);

    // ── Entity Mapping Helpers ────────────────────────────────────────────

    private static NodeEntity MapCodeNodeToEntity(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Project = node["project"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        Label = node["label"].As<string>(),
        Name = node["name"].As<string>(),
        QualifiedName = node["qualifiedName"].As<string>(),
        FilePath = GetStringOrNull(node, "filePath") ?? "",
        StartLine = node.Properties.ContainsKey("startLine") ? node["startLine"].As<int>() : 0,
        EndLine = node.Properties.ContainsKey("endLine") ? node["endLine"].As<int>() : 0,
        Properties = GetStringOrNull(node, "properties")
    };

    private static EdgeEntity MapEdgeNodeToEntity(INode node) => new()
    {
        Id = node.Properties.ContainsKey("appId") ? node["appId"].As<long>() : 0,
        Project = node["project"].As<string>(),
        SourceId = node["sourceId"].As<long>(),
        TargetId = node["targetId"].As<long>(),
        Type = node["type"].As<string>(),
        Properties = GetStringOrNull(node, "properties")
    };

    private static StoredAnalysisBatch MapAnalysisBatchNode(INode node) => new(
        node["appId"].As<long>(),
        node["repo"].As<string>(),
        node["anthropicBatchId"].As<string>(),
        node["status"].As<string>(),
        node["requestCount"].As<int>(),
        node["completedCount"].As<int>(),
        GetDateTimeOrNull(node, "submittedAt") ?? DateTime.MinValue,
        GetDateTimeOrNull(node, "completedAt"));
}
