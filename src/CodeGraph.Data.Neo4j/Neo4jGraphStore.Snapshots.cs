using CodeGraph.Models;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    public async Task<int> ReplaceRepositoryFilesAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        GraphSliceRetractions? retractions = null,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return 0;

        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            ct.ThrowIfCancellationRequested();
            var pathVariants = ExpandPathVariants(filePaths);

            // Retracted edges go first, so they are never preserved below.
            if (retractions is { Edges.Count: > 0 })
            {
                var retracted = retractions.Edges.Select(edge => new Dictionary<string, object?>
                {
                    ["source"] = edge.SourceQN,
                    ["target"] = edge.TargetQN,
                    ["type"] = edge.Type.ToString()
                }).ToList();
                foreach (var batch in Chunk(retracted, 500))
                {
                    await tx.RunAsync("""
                        UNWIND $edges AS e
                        MATCH (source:CodeNode {repository: $repository, qualifiedName: e.source})-[edge]->(target:CodeNode {repository: $repository, qualifiedName: e.target})
                        WHERE type(edge) = e.type
                        DELETE edge
                        """, new { repository, edges = batch });
                }
            }

            var incomingCursor = await tx.RunAsync("""
                MATCH (source:CodeNode)-[edge]->(target:CodeNode {repository: $repository})
                WHERE target.filePath IN $filePaths
                  // coalesce: a file-less source (null filePath) would make the predicate null and
                  // silently drop its edge instead of preserving it.
                  AND NOT (source.repository = $repository AND coalesce(source.filePath, '') IN $filePaths)
                RETURN source.qualifiedName AS sourceQN,
                       target.qualifiedName AS targetQN,
                       type(edge) AS type,
                       edge.properties AS properties
                """, new { repository, filePaths = pathVariants });
            var preservedIncoming = new List<PendingEdge>();
            await foreach (var record in incomingCursor)
            {
                if (!Enum.TryParse<EdgeType>(record["type"].As<string>(), out var type))
                    continue;
                preservedIncoming.Add(new PendingEdge(
                    record["sourceQN"].As<string>(),
                    record["targetQN"].As<string>(),
                    type,
                    DeserializeJson(record["properties"].As<string?>())));
            }

            var oldIdsCursor = await tx.RunAsync("""
                MATCH (n:CodeNode {repository: $repository})
                WHERE n.filePath IN $filePaths
                RETURN collect(n.appId) AS nodeIds
                """, new { repository, filePaths = pathVariants });
            await oldIdsCursor.FetchAsync();
            var oldIds = oldIdsCursor.Current["nodeIds"].As<List<long>>();

            await tx.RunAsync("""
                MATCH (na:NodeAnalysis)
                WHERE na.nodeId IN $nodeIds
                DETACH DELETE na
                """, new { nodeIds = oldIds });
            await tx.RunAsync("""
                MATCH (edge:CrossRepoEdge)
                WHERE edge.sourceNodeId IN $nodeIds OR edge.targetNodeId IN $nodeIds
                DETACH DELETE edge
                """, new { nodeIds = oldIds });
            await tx.RunAsync("""
                MATCH (n:CodeNode {repository: $repository})
                WHERE n.filePath IN $filePaths
                DETACH DELETE n
                """, new { repository, filePaths = pathVariants });
            await tx.RunAsync("""
                MATCH (hash:FileHash {repository: $repository})
                WHERE hash.relPath IN $filePaths
                DELETE hash
                """, new { repository, filePaths = pathVariants });

            var candidates = nodes
                .GroupBy(node => TruncateQualifiedName(node.QualifiedName), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var endpointNames = candidates.Select(node => TruncateQualifiedName(node.QualifiedName))
                .Concat(edges.SelectMany(edge => new[]
                {
                    TruncateQualifiedName(edge.SourceQN),
                    TruncateQualifiedName(edge.TargetQN)
                }))
                .Concat(preservedIncoming.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var qnToId = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var batch in Chunk(endpointNames, 1000))
            {
                var cursor = await tx.RunAsync("""
                    MATCH (n:CodeNode {repository: $repository})
                    WHERE n.qualifiedName IN $qualifiedNames
                    RETURN n.qualifiedName AS qualifiedName, n.appId AS appId
                    """, new { repository, qualifiedNames = batch });
                await foreach (var record in cursor)
                    qnToId[record["qualifiedName"].As<string>()] = record["appId"].As<long>();
            }

            var nodesToInsert = candidates
                .Where(node => !qnToId.ContainsKey(TruncateQualifiedName(node.QualifiedName)))
                .ToList();
            long startId = 0;
            if (nodesToInsert.Count > 0)
            {
                var sequenceCursor = await tx.RunAsync("""
                    MERGE (seq:Sequence {name: 'node_id'})
                    ON CREATE SET seq.value = 0
                    // Increment before reading: SET takes the write lock, so the value read back
                    // is this transaction's. Reading seq.value first loses updates under concurrency.
                    SET seq.value = seq.value + $count
                    RETURN seq.value - $count AS startId
                    """, new { count = nodesToInsert.Count });
                await sequenceCursor.FetchAsync();
                startId = sequenceCursor.Current["startId"].As<long>();
            }

            var preparedNodes = nodesToInsert.Select((node, index) => new PreparedNodeWrite(
                repository,
                node.DotnetProject,
                node.Label,
                node.Name.Length > 1000 ? node.Name[..1000] : node.Name,
                TruncateQualifiedName(node.QualifiedName),
                node.FilePath,
                node.StartLine,
                node.EndLine,
                SerializeJson(node.Properties),
                ExtractPromotedNodeProperties(node.Properties),
                node.DoNotTrust,
                startId + index + 1)).ToList();

            foreach (var batch in Chunk(preparedNodes, 100))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var group in batch.GroupBy(node => node.Label))
                {
                    var nodeLabels = GetCodeNodeSetLabels(group.Key);
                    var nodeParams = group.Select(node => new Dictionary<string, object?>
                    {
                        ["repository"] = node.Repository,
                        ["dotnetProject"] = node.DotnetProject,
                        ["label"] = node.Label.ToString(),
                        ["name"] = node.Name,
                        ["qualifiedName"] = node.QualifiedName,
                        ["filePath"] = node.FilePath,
                        ["startLine"] = node.StartLine,
                        ["endLine"] = node.EndLine,
                        ["properties"] = node.PropertiesJson,
                        ["promotedProperties"] = node.PromotedProperties,
                        ["doNotTrust"] = node.DoNotTrust,
                        ["appId"] = node.AppId
                    }).ToList();
                    await tx.RunAsync($@"
                        UNWIND $nodes AS n
                        CREATE (node:CodeNode {{repository: n.repository, qualifiedName: n.qualifiedName}})
                        SET node.appId = n.appId,
                            node.label = n.label,
                            node.name = n.name,
                            node.dotnetProject = n.dotnetProject,
                            node.filePath = n.filePath,
                            node.startLine = n.startLine,
                            node.endLine = n.endLine,
                            node.properties = n.properties,
                            node.doNotTrust = n.doNotTrust
                        SET node += n.promotedProperties
                        SET node{nodeLabels}
                        ", new { nodes = nodeParams });
                }
            }
            foreach (var node in preparedNodes)
                qnToId[node.QualifiedName] = node.AppId;

            var pendingByKey = new Dictionary<string, PendingEdge>(StringComparer.Ordinal);
            foreach (var edge in preservedIncoming)
                pendingByKey[EdgeKey(edge)] = edge;
            foreach (var edge in edges)
                pendingByKey[EdgeKey(edge)] = edge;
            var resolvedEdges = ResolveFileSliceEdges(repository, pendingByKey.Values, qnToId);
            foreach (var batch in Chunk(resolvedEdges, 100))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var group in batch.GroupBy(edge => edge.Type))
                    await UpsertRelationshipAsync(tx, group.Key, group.Select(BuildEdgeParams).ToList());
            }

            if (retractions is { OrphanCandidateQNs.Count: > 0 })
            {
                await tx.RunAsync("""
                    UNWIND $qualifiedNames AS qn
                    MATCH (n:CodeNode {repository: $repository, qualifiedName: qn})
                    WHERE (n.filePath IS NULL OR n.filePath = '') AND NOT (n)-[]-(:CodeNode)
                    DETACH DELETE n
                    """, new { repository, qualifiedNames = retractions.OrphanCandidateQNs.Distinct().ToList() });
            }

            foreach (var batch in Chunk(fileHashes.Select(hash => new Dictionary<string, object?>
                     {
                         ["repository"] = repository,
                         ["relPath"] = hash.Key,
                         ["contentHash"] = hash.Value,
                         ["extractionVersion"] = extractionVersions?.GetValueOrDefault(hash.Key)
                     }).ToList(), 200))
            {
                ct.ThrowIfCancellationRequested();
                await tx.RunAsync("""
                    UNWIND $items AS item
                    MERGE (hash:FileHash {repository: item.repository, relPath: item.relPath})
                    SET hash.contentHash = item.contentHash,
                        hash.extractionVersion = item.extractionVersion
                    """, new { items = batch });
            }

            return resolvedEdges.Count;
        });
    }

    public async Task<int> ReplaceRepositoryGraphAsync(
        string repository,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repositoryEntity,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            ct.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;
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
                    r.properties = COALESCE($properties, r.properties),
                    r.updatedAt = $now
                """, new
            {
                name = repositoryEntity.Name,
                now,
                repoUrl = repositoryEntity.RepoUrl,
                sourceGroup = repositoryEntity.SourceGroup,
                localPath = repositoryEntity.LocalPath,
                defaultBranch = repositoryEntity.DefaultBranch,
                lastCommitSha = repositoryEntity.LastCommitSha,
                language = repositoryEntity.Language,
                framework = repositoryEntity.Framework,
                isFoundational = repositoryEntity.IsFoundational,
                properties = repositoryEntity.Properties
            });

            await tx.RunAsync("""
                MATCH (n:CodeNode {repository: $repository})
                WITH collect(n.appId) AS nodeIds
                MATCH (na:NodeAnalysis)
                WHERE na.nodeId IN nodeIds
                DETACH DELETE na
                """, new { repository });
            await tx.RunAsync("""
                MATCH (e:CrossRepoEdge)
                WHERE e.sourceRepository = $repository OR e.targetRepository = $repository
                DETACH DELETE e
                """, new { repository });
            await tx.RunAsync(
                "MATCH (n:CodeNode {repository: $repository}) DETACH DELETE n",
                new { repository });
            await tx.RunAsync(
                "MATCH (f:FileHash {repository: $repository}) DELETE f",
                new { repository });

            var qnToId = new Dictionary<string, long>(nodes.Count, StringComparer.Ordinal);
            if (nodes.Count > 0)
            {
                var sequenceCursor = await tx.RunAsync("""
                    MERGE (seq:Sequence {name: 'node_id'})
                    ON CREATE SET seq.value = 0
                    // Increment before reading: SET takes the write lock, so the value read back
                    // is this transaction's. Reading seq.value first loses updates under concurrency.
                    SET seq.value = seq.value + $count
                    RETURN seq.value - $count AS startId
                    """, new { count = nodes.Count });
                await sequenceCursor.FetchAsync();
                var startId = sequenceCursor.Current["startId"].As<long>();

                var preparedNodes = nodes.Select((node, index) => new PreparedNodeWrite(
                    node.Repository,
                    node.DotnetProject,
                    node.Label,
                    node.Name.Length > 1000 ? node.Name[..1000] : node.Name,
                    node.QualifiedName.Length > 1000 ? node.QualifiedName[..1000] : node.QualifiedName,
                    node.FilePath,
                    node.StartLine,
                    node.EndLine,
                    SerializeJson(node.Properties),
                    ExtractPromotedNodeProperties(node.Properties),
                    node.DoNotTrust,
                    startId + index + 1)).ToList();

                const int neo4jBatchSize = 100;
                foreach (var batch in Chunk(preparedNodes, neo4jBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var group in batch.GroupBy(node => node.Label))
                    {
                        var nodeLabels = GetCodeNodeSetLabels(group.Key);
                        var nodeParams = group.Select(node => new Dictionary<string, object?>
                        {
                            ["repository"] = node.Repository,
                            ["dotnetProject"] = node.DotnetProject,
                            ["label"] = node.Label.ToString(),
                            ["name"] = node.Name,
                            ["qualifiedName"] = node.QualifiedName,
                            ["filePath"] = node.FilePath,
                            ["startLine"] = node.StartLine,
                            ["endLine"] = node.EndLine,
                            ["properties"] = node.PropertiesJson,
                            ["promotedProperties"] = node.PromotedProperties,
                            ["doNotTrust"] = node.DoNotTrust,
                            ["appId"] = node.AppId
                        }).ToList();

                        await tx.RunAsync($@"
                            UNWIND $nodes AS n
                            CREATE (node:CodeNode {{repository: n.repository, qualifiedName: n.qualifiedName}})
                            SET node.appId = n.appId,
                                node.label = n.label,
                                node.name = n.name,
                                node.dotnetProject = n.dotnetProject,
                                node.filePath = n.filePath,
                                node.startLine = n.startLine,
                                node.endLine = n.endLine,
                                node.properties = n.properties,
                                node.doNotTrust = n.doNotTrust
                            SET node += n.promotedProperties
                            SET node{nodeLabels}
                            ", new { nodes = nodeParams });
                    }
                }

                for (var i = 0; i < nodes.Count; i++)
                    qnToId[GraphNodeKey.Create(repository, nodes[i].QualifiedName)] = startId + i + 1;
            }

            var resolvedEdges = ResolveSnapshotEdges(repository, edges, qnToId);
            const int edgeBatchSize = 100;
            foreach (var batch in Chunk(resolvedEdges, edgeBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var group in batch.GroupBy(edge => edge.Type))
                    await UpsertRelationshipAsync(tx, group.Key, group.Select(BuildEdgeParams).ToList());
            }

            if (fileHashes.Count > 0)
            {
                var hashItems = fileHashes.Select(hash => new Dictionary<string, object?>
                {
                    ["repository"] = repository,
                    ["relPath"] = hash.Key,
                    ["contentHash"] = hash.Value,
                    ["extractionVersion"] = extractionVersions?.GetValueOrDefault(hash.Key)
                }).ToList();

                const int hashBatchSize = 200;
                foreach (var batch in Chunk(hashItems, hashBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    await tx.RunAsync("""
                        UNWIND $items AS h
                        CREATE (f:FileHash {repository: h.repository, relPath: h.relPath, contentHash: h.contentHash,
                                            extractionVersion: h.extractionVersion})
                        """, new { items = batch });
                }
            }

            if (syncState is not null)
            {
                await tx.RunAsync("""
                    MERGE (s:SyncState {repository: $repository})
                    SET s.lastSyncAt = $lastSyncAt,
                        s.lastCommitSha = COALESCE($lastCommitSha, s.lastCommitSha),
                        s.extractionFingerprint = COALESCE($extractionFingerprint, s.extractionFingerprint),
                        s.status = $status,
                        s.errorMessage = $errorMessage
                    """, new
                {
                    repository = syncState.Repository,
                    extractionFingerprint = syncState.ExtractionFingerprint,
                    lastSyncAt = syncState.LastSyncAt,
                    lastCommitSha = syncState.LastCommitSha,
                    status = syncState.Status,
                    errorMessage = syncState.ErrorMessage
                });
            }

            return resolvedEdges.Count;
        });
    }

    public async Task DeleteFilesFromRepositoryGraphAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return;

        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var pathVariants = ExpandPathVariants(filePaths);
            foreach (var batch in Chunk(pathVariants, 1000))
            {
                ct.ThrowIfCancellationRequested();
                await tx.RunAsync("""
                    MATCH (n:CodeNode {repository: $repository})
                    WHERE n.filePath IN $filePaths
                    WITH collect(n.appId) AS nodeIds
                    MATCH (na:NodeAnalysis)
                    WHERE na.nodeId IN nodeIds
                    DETACH DELETE na
                    """, new { repository, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (n:CodeNode {repository: $repository})
                    WHERE n.filePath IN $filePaths
                    WITH collect(n.appId) AS nodeIds
                    MATCH (e:CrossRepoEdge)
                    WHERE e.sourceNodeId IN nodeIds OR e.targetNodeId IN nodeIds
                    DETACH DELETE e
                    """, new { repository, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (n:CodeNode {repository: $repository})
                    WHERE n.filePath IN $filePaths
                    DETACH DELETE n
                    """, new { repository, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (f:FileHash {repository: $repository})
                    WHERE f.relPath IN $filePaths
                    DELETE f
                    """, new { repository, filePaths = batch });
            }
        });
    }

    private static List<string> ExpandPathVariants(IReadOnlyList<string> filePaths) =>
        filePaths
            .SelectMany(path => new[]
            {
                path.Replace('\\', '/'),
                path.Replace('/', '\\')
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<GraphEdge> ResolveSnapshotEdges(
        string repository,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(GraphNodeKey.Create(repository, edge.SourceQN), out var sourceId) ||
                !qnToId.TryGetValue(GraphNodeKey.Create(repository, edge.TargetQN), out var targetId))
            {
                continue;
            }

            resolved.Add(new GraphEdge
            {
                Repository = repository,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }

        return resolved;
    }

    private static List<GraphEdge> ResolveFileSliceEdges(
        string repository,
        IEnumerable<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>();
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(TruncateQualifiedName(edge.SourceQN), out var sourceId) ||
                !qnToId.TryGetValue(TruncateQualifiedName(edge.TargetQN), out var targetId))
                continue;
            resolved.Add(new GraphEdge
            {
                Repository = repository,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }
        return resolved;
    }

    private static string TruncateQualifiedName(string qualifiedName) =>
        qualifiedName.Length > 1000 ? qualifiedName[..1000] : qualifiedName;

    private static string EdgeKey(PendingEdge edge) =>
        $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";
}
