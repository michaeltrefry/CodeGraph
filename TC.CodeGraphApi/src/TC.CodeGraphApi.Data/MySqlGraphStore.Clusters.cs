using System.Text;
using Dapper;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── Clusters — community detection results ────────────────────────────

    public async Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters)
    {
        await using var conn = await GetOpenConnectionAsync();

        // Clear existing clusters and rewrite
        await conn.ExecuteAsync("DELETE FROM repo_clusters");

        if (clusters.Count == 0) return;

        foreach (var batch in Chunk(clusters, options.BatchSize))
        {
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO repo_clusters (project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at)
                VALUES
                """);

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendLine($"(@ProjectName{i}, @ClusterId{i}, @ClusterLabel{i}, @Modularity{i}, @Level{i}, @Betweenness{i}, @ComputedAt{i})");

                var c = batch[i];
                parameters.Add($"ProjectName{i}", c.ProjectName);
                parameters.Add($"ClusterId{i}", c.ClusterId);
                parameters.Add($"ClusterLabel{i}", c.ClusterLabel);
                parameters.Add($"Modularity{i}", c.ModularityScore);
                parameters.Add($"Level{i}", c.Level);
                parameters.Add($"Betweenness{i}", c.BetweennessCentrality);
                parameters.Add($"ComputedAt{i}", c.ComputedAt);
            }

            var sql = sb.ToString();
            await conn.ExecuteAsync(sql, parameters);
        }
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0)
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<RepoCluster>(
            "SELECT id, project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at FROM repo_clusters WHERE level = @Level ORDER BY cluster_id, project_name",
            new { Level = level });
        return results.ToList();
    }

    public async Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0)
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<RepoCluster>(
            "SELECT id, project_name, cluster_id, cluster_label, modularity_score, level, betweenness_centrality, computed_at FROM repo_clusters WHERE cluster_id = @ClusterId AND level = @Level ORDER BY betweenness_centrality DESC",
            new { ClusterId = clusterId, Level = level });
        return results.ToList();
    }
}
