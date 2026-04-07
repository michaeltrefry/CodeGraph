using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Nodes ─────────────────────────────────────────────────────────────

    public async Task<long> UpsertNodeAsync(GraphNode node)
    {
        var nodeName = node.Name.Length > 1000 ? node.Name[..1000] : node.Name;
        var nodeQualifiedName = node.QualifiedName.Length > 1000 ? node.QualifiedName[..1000] : node.QualifiedName;
        var nodeLabels = GetCodeNodeSetLabels(node.Label);
        var promotedProperties = ExtractPromotedNodeProperties(node.Properties);

        await using var session = sessionFactory.GetSession();

        return await session.ExecuteWriteAsync(async tx =>
        {
            var cypher = $@"
                MERGE (seq:Sequence {{name: 'node_id'}})
                ON CREATE SET seq.value = 0
                SET seq.value = seq.value + 1
                WITH seq.value AS newId
                MERGE (n:CodeNode {{project: $project, qualifiedName: $qualifiedName}})
                ON CREATE SET n.appId = newId
                SET n.label = $label,
                    n.name = $name,
                    n.dotnetProject = $dotnetProject,
                    n.filePath = $filePath,
                    n.startLine = $startLine,
                    n.endLine = $endLine,
                    n.properties = $properties,
                    n.doNotTrust = $doNotTrust
                SET n += $promotedProperties
                SET n{nodeLabels}
                RETURN n.appId AS appId
                ";

            var cursor = await tx.RunAsync(cypher,
                new
                {
                    project = node.Project,
                    qualifiedName = nodeQualifiedName,
                    label = node.Label.ToString(),
                    name = nodeName,
                    dotnetProject = node.DotnetProject,
                    filePath = node.FilePath,
                    startLine = node.StartLine,
                    endLine = node.EndLine,
                    properties = SerializeJson(node.Properties),
                    doNotTrust = node.DoNotTrust,
                    promotedProperties
                });

            await cursor.FetchAsync();
            return cursor.Current["appId"].As<long>();
        });
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(
        IReadOnlyList<GraphNode> nodes, CancellationToken ct = default)
    {
        if (nodes.Count == 0) return new Dictionary<string, long>();

        var result = new Dictionary<string, long>(nodes.Count);
        await using var session = sessionFactory.GetSession();

        // Allocate all IDs up front in a single lightweight transaction to avoid
        // lock contention on the Sequence node during the heavier MERGE batches.
        var seqSw = Stopwatch.StartNew();
        var totalStartId = await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (seq:Sequence {name: 'node_id'})
                ON CREATE SET seq.value = 0
                WITH seq.value AS startId, seq
                SET seq.value = seq.value + $count
                RETURN startId
                """,
                new { count = nodes.Count });

            await cursor.FetchAsync();
            return cursor.Current["startId"].As<long>();
        });
        logger.LogInformation("[Timing] Neo4j sequence allocation: {ElapsedMs}ms", seqSw.ElapsedMilliseconds);

        // Use smaller batches for Neo4j — large UNWIND+MERGE transactions are expensive
        var preparedNodes = nodes.Select((n, i) => new PreparedNodeWrite(
            n.Project,
            n.DotnetProject,
            n.Label,
            n.Name.Length > 1000 ? n.Name[..1000] : n.Name,
            n.QualifiedName.Length > 1000 ? n.QualifiedName[..1000] : n.QualifiedName,
            n.FilePath,
            n.StartLine,
            n.EndLine,
            SerializeJson(n.Properties),
            ExtractPromotedNodeProperties(n.Properties),
            n.DoNotTrust,
            totalStartId + i + 1)).ToList();

        const int neo4jBatchSize = 100;
        var batchNum = 0;

        foreach (var batch in Chunk(preparedNodes, neo4jBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            batchNum++;
            var batchSw = Stopwatch.StartNew();

            var batchResult = new List<(string qn, long id)>();

            foreach (var group in batch.GroupBy(n => n.Label))
            {
                var nodeLabels = GetCodeNodeSetLabels(group.Key);
                var nodeParams = group.Select(n => new Dictionary<string, object?>
                {
                    ["project"] = n.Project,
                    ["dotnetProject"] = n.DotnetProject,
                    ["label"] = n.Label.ToString(),
                    ["name"] = n.Name,
                    ["qualifiedName"] = n.QualifiedName,
                    ["filePath"] = n.FilePath,
                    ["startLine"] = n.StartLine,
                    ["endLine"] = n.EndLine,
                    ["properties"] = n.PropertiesJson,
                    ["promotedProperties"] = n.PromotedProperties,
                    ["doNotTrust"] = n.DoNotTrust,
                    ["appId"] = n.AppId
                }).ToList();

                var groupResult = await session.ExecuteWriteAsync(async tx =>
                {
                    var cypher = $@"
                        UNWIND $nodes AS n
                        MERGE (node:CodeNode {{project: n.project, qualifiedName: n.qualifiedName}})
                        ON CREATE SET node.appId = n.appId
                        SET node.label = n.label,
                            node.name = n.name,
                            node.dotnetProject = n.dotnetProject,
                            node.filePath = n.filePath,
                            node.startLine = n.startLine,
                            node.endLine = n.endLine,
                            node.properties = n.properties,
                            node.doNotTrust = n.doNotTrust
                        SET node += n.promotedProperties
                        SET node{nodeLabels}
                        RETURN node.qualifiedName AS qualifiedName, node.appId AS appId
                        ";

                    var resultCursor = await tx.RunAsync(cypher,
                        new { nodes = nodeParams });

                    var pairs = new List<(string qn, long id)>();
                    await foreach (var record in resultCursor)
                        pairs.Add((record["qualifiedName"].As<string>(), record["appId"].As<long>()));
                    return pairs;
                });

                batchResult.AddRange(groupResult);
            }

            logger.LogInformation("[Timing] Neo4j node batch {BatchNum} ({BatchSize} nodes): {ElapsedMs}ms",
                batchNum, batch.Count, batchSw.ElapsedMilliseconds);

            foreach (var (qn, id) in batchResult)
                result[qn] = id;
        }

        return result;
    }

    public async Task<GraphNode?> FindNodeByIdAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {appId: $id}) RETURN n",
                new { id });
            if (await cursor.FetchAsync())
                return MapCodeNode(cursor.Current["n"].As<INode>());
            return null;
        });
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project, qualifiedName: $qualifiedName}) RETURN n",
                new { project, qualifiedName });
            if (await cursor.FetchAsync())
                return MapCodeNode(cursor.Current["n"].As<INode>());
            return null;
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project, name: $name}) RETURN n LIMIT $limit",
                new { project, name, limit });
            var results = new List<GraphNode>();
            await foreach (var record in cursor)
                results.Add(MapCodeNode(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH {GetCodeNodeMatchPattern("n", label)} WHERE n.project = $project RETURN n LIMIT $limit",
                new { project, limit });
            var results = new List<GraphNode>();
            await foreach (var record in cursor)
                results.Add(MapCodeNode(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project, filePath: $filePath}) RETURN n LIMIT $limit",
                new { project, filePath, limit });
            var results = new List<GraphNode>();
            await foreach (var record in cursor)
                results.Add(MapCodeNode(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0, string? dotnetProject = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            // Use fulltext index for non-wildcard patterns; fall back to label scan for match-all
            var useFulltext = !string.IsNullOrEmpty(namePattern) && namePattern != "%";

            string cypher;
            var parameters = new Dictionary<string, object?>();

            if (useFulltext)
            {
                // Escape Lucene special characters and use wildcard search
                var lucenePattern = "*" + EscapeLucene(namePattern) + "*";
                cypher = "CALL db.index.fulltext.queryNodes('code_node_search', $pattern) YIELD node AS n";
                parameters["pattern"] = lucenePattern;
            }
            else
            {
                cypher = $"MATCH {GetCodeNodeMatchPattern("n", label)}";
            }

            var whereAdded = false;
            void AddWhere(string clause)
            {
                cypher += whereAdded ? " AND " : " WHERE ";
                cypher += clause;
                whereAdded = true;
            }

            if (project is not null)
            {
                AddWhere("n.project = $project");
                parameters["project"] = project;
            }
            if (label is not null && useFulltext)
            {
                AddWhere("n.label = $label");
                parameters["label"] = label.Value.ToString();
            }
            if (filePattern is not null)
            {
                AddWhere("n.filePath CONTAINS $filePattern");
                parameters["filePattern"] = filePattern;
            }
            if (dotnetProject is not null)
            {
                AddWhere("n.dotnetProject = $dotnetProject");
                parameters["dotnetProject"] = dotnetProject;
            }

            cypher += " RETURN n ORDER BY n.name SKIP $offset LIMIT $limit";
            parameters["offset"] = offset;
            parameters["limit"] = limit;

            var cursor = await tx.RunAsync(cypher, parameters);
            var results = new List<GraphNode>();
            await foreach (var record in cursor)
                results.Add(MapCodeNode(record["n"].As<INode>()));
            return results;
        });
    }

    private static string EscapeLucene(string input)
    {
        // Escape Lucene special characters: + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is '+' or '-' or '!' or '(' or ')' or '{' or '}' or '[' or ']'
                or '^' or '"' or '~' or '*' or '?' or ':' or '\\' or '/')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public async Task<int> SearchNodesCountAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var useFulltext = !string.IsNullOrEmpty(namePattern) && namePattern != "%";
            string cypher;
            var parameters = new Dictionary<string, object?>();

            if (useFulltext)
            {
                var lucenePattern = "*" + EscapeLucene(namePattern) + "*";
                cypher = "CALL db.index.fulltext.queryNodes('code_node_search', $pattern) YIELD node AS n";
                parameters["pattern"] = lucenePattern;
            }
            else
            {
                cypher = $"MATCH {GetCodeNodeMatchPattern("n", label)}";
            }

            var whereAdded = false;
            void AddWhere(string clause)
            {
                cypher += whereAdded ? " AND " : " WHERE ";
                cypher += clause;
                whereAdded = true;
            }

            if (project is not null)
            {
                AddWhere("n.project = $project");
                parameters["project"] = project;
            }
            if (label is not null && useFulltext)
            {
                AddWhere("n.label = $label");
                parameters["label"] = label.Value.ToString();
            }
            if (filePattern is not null)
            {
                AddWhere("n.filePath CONTAINS $filePattern");
                parameters["filePattern"] = filePattern;
            }
            if (dotnetProject is not null)
            {
                AddWhere("n.dotnetProject = $dotnetProject");
                parameters["dotnetProject"] = dotnetProject;
            }

            cypher += " RETURN count(n) AS total";

            var cursor = await tx.RunAsync(cypher, parameters);
            await cursor.FetchAsync();
            return cursor.Current["total"].As<int>();
        });
    }

    public async Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (n:CodeNode {appId: $nodeId}) SET n.doNotTrust = $doNotTrust",
                new { nodeId, doNotTrust });
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH {GetCodeNodeMatchPattern("n", label)} RETURN n LIMIT $limit",
                new { limit });
            var results = new List<GraphNode>();
            await foreach (var record in cursor)
                results.Add(MapCodeNode(record["n"].As<INode>()));
            return results;
        });
    }

    public async Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode) RETURN n.label AS label, count(n) AS count");
            var result = new Dictionary<NodeLabel, int>();
            await foreach (var record in cursor)
            {
                var labelStr = record["label"].As<string>();
                if (Enum.TryParse<NodeLabel>(labelStr, out var lbl))
                    result[lbl] = record["count"].As<int>();
            }
            return result;
        });
    }

    public async Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return new Dictionary<long, GraphNode>();

        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var result = new Dictionary<long, GraphNode>(ids.Count);

            foreach (var chunk in Chunk(ids, 1000))
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:CodeNode) WHERE n.appId IN $ids RETURN n",
                    new { ids = chunk });
                await foreach (var record in cursor)
                {
                    var node = MapCodeNode(record["n"].As<INode>());
                    result[node.Id] = node;
                }
            }
            return result;
        });
    }

    public async Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (n:CodeNode {project: $project})
                WHERE n.dotnetProject IS NOT NULL
                RETURN n.dotnetProject AS dotnetProject, n.label AS label, count(n) AS count
                """,
                new { project });

            var result = new Dictionary<string, Dictionary<string, int>>();
            await foreach (var record in cursor)
            {
                var dp = record["dotnetProject"].As<string>();
                var lbl = record["label"].As<string>();
                var cnt = record["count"].As<int>();
                if (!result.TryGetValue(dp, out var labelCounts))
                {
                    labelCounts = new Dictionary<string, int>();
                    result[dp] = labelCounts;
                }
                labelCounts[lbl] = cnt;
            }
            return result;
        });
    }

    public async Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project}) RETURN n.label AS label, count(n) AS count",
                new { project });
            var result = new Dictionary<string, int>();
            await foreach (var record in cursor)
                result[record["label"].As<string>()] = record["count"].As<int>();
            return result;
        });
    }

    // ── Bulk Delete ───────────────────────────────────────────────────────

    public async Task DeleteNodesByFileAsync(string project, string filePath)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (n:CodeNode {project: $project, filePath: $filePath}) DETACH DELETE n",
                new { project, filePath });
        });
    }

    public async Task DeleteNodesByProjectAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        // Delete in batches to avoid transaction memory issues on large projects
        var deleted = 0;
        int batchDeleted;
        do
        {
            batchDeleted = await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync("""
                    MATCH (n:CodeNode {project: $project})
                    WITH n LIMIT 5000
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { project });
                await cursor.FetchAsync();
                return cursor.Current["deleted"].As<int>();
            });
            deleted += batchDeleted;
        } while (batchDeleted > 0);
    }

    private sealed record PreparedNodeWrite(
        string Project,
        string? DotnetProject,
        NodeLabel Label,
        string Name,
        string QualifiedName,
        string FilePath,
        int StartLine,
        int EndLine,
        string? PropertiesJson,
        Dictionary<string, object> PromotedProperties,
        bool DoNotTrust,
        long AppId);
}
