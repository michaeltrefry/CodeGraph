using CodeGraph.Models;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    public async Task<int> ReplaceProjectGraphAsync(
        string project,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repository,
        SyncStateEntity? syncState,
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
                properties = repository.Properties
            });

            await tx.RunAsync("""
                MATCH (n:CodeNode {project: $project})
                WITH collect(n.appId) AS nodeIds
                MATCH (na:NodeAnalysis)
                WHERE na.nodeId IN nodeIds
                DETACH DELETE na
                """, new { project });
            await tx.RunAsync("""
                MATCH (e:CrossRepoEdge)
                WHERE e.sourceProject = $project OR e.targetProject = $project
                DETACH DELETE e
                """, new { project });
            await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project}) DETACH DELETE n",
                new { project });
            await tx.RunAsync(
                "MATCH (f:FileHash {project: $project}) DELETE f",
                new { project });

            var qnToId = new Dictionary<string, long>(nodes.Count, StringComparer.OrdinalIgnoreCase);
            if (nodes.Count > 0)
            {
                var sequenceCursor = await tx.RunAsync("""
                    MERGE (seq:Sequence {name: 'node_id'})
                    ON CREATE SET seq.value = 0
                    WITH seq.value AS startId, seq
                    SET seq.value = seq.value + $count
                    RETURN startId
                    """, new { count = nodes.Count });
                await sequenceCursor.FetchAsync();
                var startId = sequenceCursor.Current["startId"].As<long>();

                var preparedNodes = nodes.Select((node, index) => new PreparedNodeWrite(
                    node.Project,
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
                            ["project"] = node.Project,
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
                            CREATE (node:CodeNode {{project: n.project, qualifiedName: n.qualifiedName}})
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
                    qnToId[GraphNodeKey.Create(project, nodes[i].QualifiedName)] = startId + i + 1;
            }

            var resolvedEdges = ResolveSnapshotEdges(project, edges, qnToId);
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
                    ["project"] = project,
                    ["relPath"] = hash.Key,
                    ["contentHash"] = hash.Value
                }).ToList();

                const int hashBatchSize = 200;
                foreach (var batch in Chunk(hashItems, hashBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    await tx.RunAsync("""
                        UNWIND $items AS h
                        CREATE (f:FileHash {project: h.project, relPath: h.relPath, contentHash: h.contentHash})
                        """, new { items = batch });
                }
            }

            if (syncState is not null)
            {
                await tx.RunAsync("""
                    MERGE (s:SyncState {project: $project})
                    SET s.lastSyncAt = $lastSyncAt,
                        s.lastCommitSha = COALESCE($lastCommitSha, s.lastCommitSha),
                        s.status = $status,
                        s.errorMessage = $errorMessage
                    """, new
                {
                    project = syncState.Project,
                    lastSyncAt = syncState.LastSyncAt,
                    lastCommitSha = syncState.LastCommitSha,
                    status = syncState.Status,
                    errorMessage = syncState.ErrorMessage
                });
            }

            return resolvedEdges.Count;
        });
    }

    public async Task DeleteFilesFromProjectGraphAsync(
        string project,
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
                    MATCH (n:CodeNode {project: $project})
                    WHERE n.filePath IN $filePaths
                    WITH collect(n.appId) AS nodeIds
                    MATCH (na:NodeAnalysis)
                    WHERE na.nodeId IN nodeIds
                    DETACH DELETE na
                    """, new { project, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (n:CodeNode {project: $project})
                    WHERE n.filePath IN $filePaths
                    WITH collect(n.appId) AS nodeIds
                    MATCH (e:CrossRepoEdge)
                    WHERE e.sourceNodeId IN nodeIds OR e.targetNodeId IN nodeIds
                    DETACH DELETE e
                    """, new { project, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (n:CodeNode {project: $project})
                    WHERE n.filePath IN $filePaths
                    DETACH DELETE n
                    """, new { project, filePaths = batch });
                await tx.RunAsync("""
                    MATCH (f:FileHash {project: $project})
                    WHERE f.relPath IN $filePaths
                    DELETE f
                    """, new { project, filePaths = batch });
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
        string project,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(GraphNodeKey.Create(project, edge.SourceQN), out var sourceId) ||
                !qnToId.TryGetValue(GraphNodeKey.Create(project, edge.TargetQN), out var targetId))
            {
                continue;
            }

            resolved.Add(new GraphEdge
            {
                Project = project,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }

        return resolved;
    }
}
