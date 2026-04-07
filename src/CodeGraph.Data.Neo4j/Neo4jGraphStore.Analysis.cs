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
                MATCH (n:CodeNode:Type {project: $project})
                WHERE (n:Class OR n:Interface)
                  AND EXISTS { MATCH (n)-[]-(:CodeNode) }
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
                MATCH (:CodeNode {appId: $parentNodeId})-[:DEFINES|DEFINES_METHOD]->(n:CodeNode)
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
                MATCH (source:CodeNode {appId: $nodeId})-[e]->(target:CodeNode)
                WHERE NOT type(e) IN ['DEFINES', 'DEFINES_METHOD', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE', 'CONTAINS_PROJECT']
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                ORDER BY type
                """,
                new { nodeId });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecordToEntity(record));
            return results;
        });
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (source:CodeNode)-[e]->(target:CodeNode {appId: $nodeId})
                WHERE NOT type(e) IN ['DEFINES', 'DEFINES_METHOD', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE', 'CONTAINS_PROJECT']
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                ORDER BY type
                """,
                new { nodeId });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecordToEntity(record));
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
                """
                MATCH (source:CodeNode {project: $project})-[e]->(target:CodeNode)
                WHERE coalesce(e.project, source.project) = $project
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                """,
                new { project });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecordToEntity(record));
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
                MATCH (source:CodeNode)-[e]->(target:CodeNode)
                WHERE source.appId IN $nodeIds OR target.appId IN $nodeIds
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                """,
                new { nodeIds });
            var results = new List<EdgeEntity>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecordToEntity(record));
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
                    providerBatchId: $providerBatchId,
                    providerName: $providerName,
                    executionMode: $executionMode,
                    includeAllSource: $includeAllSource,
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
                    providerBatchId = batch.ProviderBatchId,
                    providerName = batch.ProviderName,
                    executionMode = batch.ExecutionMode,
                    includeAllSource = batch.IncludeAllSource,
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
            ["sequence"] = r.Sequence,
            ["customId"] = r.CustomId,
            ["nodeId"] = r.NodeId,
            ["nodeLabel"] = r.NodeLabel,
            ["requestPayloadJson"] = r.RequestPayloadJson,
            ["status"] = r.Status,
            ["attemptCount"] = r.AttemptCount,
            ["responseText"] = r.ResponseText,
            ["modelUsed"] = r.ModelUsed,
            ["completedAt"] = (object?)r.CompletedAt
        }).ToList();

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                UNWIND $items AS r
                CREATE (br:AnalysisBatchRequest {
                    batchId: r.batchId,
                    sequence: r.sequence,
                    customId: r.customId,
                    nodeId: r.nodeId,
                    nodeLabel: r.nodeLabel,
                    requestPayloadJson: r.requestPayloadJson,
                    status: r.status,
                    attemptCount: r.attemptCount,
                    responseText: r.responseText,
                    modelUsed: r.modelUsed,
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

    public async Task<StoredAnalysisBatch?> GetBatchByProviderBatchIdAsync(string providerBatchId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (b:AnalysisBatch {providerBatchId: $providerBatchId}) RETURN b ORDER BY b.submittedAt DESC LIMIT 1",
                new { providerBatchId });
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

    public async Task UpdateBatchRequestStateAsync(long batchId, string customId, string status, int attemptCount,
        string? responseText, string? modelUsed, DateTime? completedAt)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (br:AnalysisBatchRequest {batchId: $batchId, customId: $customId})
                SET br.status = $status,
                    br.attemptCount = $attemptCount,
                    br.responseText = $responseText,
                    br.modelUsed = $modelUsed,
                    br.completedAt = $completedAt
                """,
                new { batchId, customId, status, attemptCount, responseText = (object?)responseText, modelUsed = (object?)modelUsed, completedAt = (object?)completedAt });
        });
    }

    public async Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (br:AnalysisBatchRequest {batchId: $batchId}) RETURN br ORDER BY coalesce(br.sequence, 0), br.customId",
                new { batchId });
            var results = new List<AnalysisBatchRequestEntity>();
            await foreach (var record in cursor)
            {
                var node = record["br"].As<INode>();
                results.Add(new AnalysisBatchRequestEntity
                {
                    BatchId = node["batchId"].As<long>(),
                    Sequence = node.Properties.ContainsKey("sequence") ? node["sequence"].As<int>() : 0,
                    CustomId = node["customId"].As<string>(),
                    NodeId = node.Properties.ContainsKey("nodeId") ? node["nodeId"].As<long?>() : null,
                    NodeLabel = node["nodeLabel"].As<string>(),
                    RequestPayloadJson = node.Properties.ContainsKey("requestPayloadJson") ? node["requestPayloadJson"].As<string?>() : null,
                    Status = node["status"].As<string>(),
                    AttemptCount = node.Properties.ContainsKey("attemptCount") ? node["attemptCount"].As<int>() : 0,
                    ResponseText = node.Properties.ContainsKey("responseText") ? node["responseText"].As<string?>() : null,
                    ModelUsed = node.Properties.ContainsKey("modelUsed") ? node["modelUsed"].As<string?>() : null,
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
        Label = GetNodeLabel(node).ToString(),
        Name = node["name"].As<string>(),
        QualifiedName = node["qualifiedName"].As<string>(),
        FilePath = GetStringOrNull(node, "filePath") ?? "",
        StartLine = node.Properties.ContainsKey("startLine") ? node["startLine"].As<int>() : 0,
        EndLine = node.Properties.ContainsKey("endLine") ? node["endLine"].As<int>() : 0,
        Properties = GetStringOrNull(node, "properties")
    };

    private static EdgeEntity MapEdgeRecordToEntity(IRecord record) => new()
    {
        Id = GetRelationshipId(record),
        Project = record["project"].As<string>(),
        SourceId = record["sourceId"].As<long>(),
        TargetId = record["targetId"].As<long>(),
        Type = record["type"].As<string>(),
        Properties = record["properties"].As<string?>()
    };

    private static StoredAnalysisBatch MapAnalysisBatchNode(INode node) => new(
        node["appId"].As<long>(),
        node["repo"].As<string>(),
        node["providerBatchId"].As<string>(),
        GetStringOrNull(node, "providerName") ?? "anthropic",
        GetStringOrNull(node, "executionMode") ?? "native_batch",
        node.Properties.ContainsKey("includeAllSource") && node["includeAllSource"].As<bool>(),
        node["status"].As<string>(),
        node["requestCount"].As<int>(),
        node["completedCount"].As<int>(),
        GetDateTimeOrNull(node, "submittedAt") ?? DateTime.MinValue,
        GetDateTimeOrNull(node, "completedAt"));
}
