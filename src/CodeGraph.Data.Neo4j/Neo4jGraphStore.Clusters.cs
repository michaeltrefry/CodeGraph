using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Clusters (community detection results) ────────────────────────────

    public async Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters)
    {
        await using var session = sessionFactory.GetSession();

        // Clear existing clusters
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("MATCH (c:RepoCluster) DELETE c");
        });

        if (clusters.Count == 0) return;

        foreach (var batch in Chunk(clusters, options.BatchSize))
        {
            var items = batch.Select(c => new Dictionary<string, object?>
            {
                ["projectName"] = c.ProjectName,
                ["clusterId"] = c.ClusterId,
                ["clusterLabel"] = c.ClusterLabel,
                ["modularityScore"] = (double)c.ModularityScore,
                ["level"] = c.Level,
                ["betweennessCentrality"] = (double)c.BetweennessCentrality,
                ["computedAt"] = c.ComputedAt
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS c
                    CREATE (rc:RepoCluster {
                        projectName: c.projectName,
                        clusterId: c.clusterId,
                        clusterLabel: c.clusterLabel,
                        modularityScore: c.modularityScore,
                        level: c.level,
                        betweennessCentrality: c.betweennessCentrality,
                        computedAt: c.computedAt
                    })
                    """,
                    new { items });
            });
        }
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (c:RepoCluster {level: $level}) RETURN c ORDER BY c.clusterId, c.projectName",
                new { level });
            var results = new List<RepoCluster>();
            await foreach (var record in cursor)
                results.Add(MapClusterNode(record["c"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (c:RepoCluster {clusterId: $clusterId, level: $level}) RETURN c ORDER BY c.betweennessCentrality DESC",
                new { clusterId, level });
            var results = new List<RepoCluster>();
            await foreach (var record in cursor)
                results.Add(MapClusterNode(record["c"].As<INode>()));
            return results;
        });
    }

    private static RepoCluster MapClusterNode(INode node) => new()
    {
        ProjectName = node["projectName"].As<string>(),
        ClusterId = node["clusterId"].As<int>(),
        ClusterLabel = GetStringOrNull(node, "clusterLabel"),
        ModularityScore = node.Properties.ContainsKey("modularityScore") ? (decimal)node["modularityScore"].As<double>() : 0,
        Level = node.Properties.ContainsKey("level") ? node["level"].As<int>() : 0,
        BetweennessCentrality = node.Properties.ContainsKey("betweennessCentrality") ? (decimal)node["betweennessCentrality"].As<double>() : 0,
        ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
    };
}
