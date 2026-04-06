using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public class CommunityDetectionService(IGraphStore store, ILogger<CommunityDetectionService> logger)
    : ICommunityDetectionService
{
    // Edge type weights — messaging is strongest coupling
    private static readonly Dictionary<string, double> EdgeWeights = new()
    {
        ["PUBLISHES"] = 3.0,
        ["CONSUMES"] = 3.0,
        ["HTTP_CALLS"] = 2.0,
        ["REFERENCES_PACKAGE"] = 1.0,
    };

    private const double DefaultWeight = 1.0;
    private const double BidirectionalBonus = 1.5;

    public async Task DetectCommunitiesAsync(CancellationToken ct = default)
    {
        var repos = await store.ListRepositoriesAsync();
        var allEdges = await store.GetAllCrossRepoEdgesAsync();

        ct.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Community detection input: {RepoCount} repos, {EdgeCount} cross-repo edges",
            repos.Count, allEdges.Count);

        // Build weighted adjacency list from all cross-repo edges
        var adjacency = BuildWeightedAdjacency(allEdges);

        if (adjacency.Count == 0)
        {
            logger.LogWarning(
                "No cross-repo edges found — skipping community detection. Total edges: {EdgeCount}",
                allEdges.Count);
            await store.ReplaceRepoClustersAsync([]);
            return;
        }

        // Run Louvain
        var result = LouvainAlgorithm.Execute(adjacency);
        logger.LogInformation(
            "Community detection complete: {Communities} communities, modularity={Modularity:F4}, {Nodes} projects",
            result.CommunityCount, result.Modularity, result.Communities.Count);

        // Compute betweenness centrality
        var centrality = LouvainAlgorithm.ComputeBetweennessCentrality(adjacency);

        // Map to RepoCluster records
        var now = DateTime.UtcNow;
        var clusters = result.Communities.Select(kv => new RepoCluster
        {
            ProjectName = kv.Key,
            ClusterId = kv.Value,
            ModularityScore = (decimal)result.Modularity,
            Level = 0,
            BetweennessCentrality = (decimal)centrality.GetValueOrDefault(kv.Key, 0),
            ComputedAt = now
        }).ToList();

        await store.ReplaceRepoClustersAsync(clusters);

        ct.ThrowIfCancellationRequested();
    }

    public async Task<ClusterOverviewResponse> GetClusterOverviewAsync()
    {
        var clusters = await store.GetRepoClustersAsync();
        var allEdges = await store.GetAllCrossRepoEdgesAsync();
        var repos = await store.ListRepositoriesAsync();

        if (clusters.Count == 0)
            return new ClusterOverviewResponse([], 0, repos.Count, 0, null);

        // Build lookup: project → cluster_id (case-insensitive — edge project names may differ in casing)
        var projectCluster = clusters.GroupBy(c => c.ProjectName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ClusterId, StringComparer.OrdinalIgnoreCase);

        // Group by cluster
        var grouped = clusters.GroupBy(c => c.ClusterId).OrderBy(g => g.Key);
        var summaries = new List<ClusterSummary>();

        foreach (var group in grouped)
        {
            var members = group.Select(c => c.ProjectName).ToList();
            var memberSet = members.ToHashSet(StringComparer.OrdinalIgnoreCase);

            int internalEdges = 0, externalEdges = 0;
            foreach (var edge in allEdges)
            {
                bool sourceIn = memberSet.Contains(edge.SourceProject);
                bool targetIn = memberSet.Contains(edge.TargetProject);
                if (sourceIn && targetIn) internalEdges++;
                else if (sourceIn || targetIn) externalEdges++;
            }

            // Density = actual internal edges / possible internal edges
            int possibleEdges = members.Count * (members.Count - 1) / 2;
            double density = possibleEdges > 0 ? (double)internalEdges / possibleEdges : 0;

            // Bridge repos: top betweenness centrality within cluster
            var bridgeRepos = group
                .Where(c => c.BetweennessCentrality > 0.01m)
                .OrderByDescending(c => c.BetweennessCentrality)
                .Take(3)
                .Select(c => c.ProjectName)
                .ToList();

            summaries.Add(new ClusterSummary(
                group.Key,
                group.First().ClusterLabel,
                members,
                internalEdges,
                externalEdges,
                density,
                bridgeRepos));
        }

        return new ClusterOverviewResponse(
            summaries,
            (double)clusters.First().ModularityScore,
            repos.Count,
            clusters.Count,
            clusters.First().ComputedAt);
    }

    public async Task<ClusterDetailResponse?> GetClusterDetailAsync(int clusterId)
    {
        var members = await store.GetRepoClusterMembersAsync(clusterId);
        if (members.Count == 0) return null;

        var allEdges = await store.GetAllCrossRepoEdgesAsync();
        var allClusters = await store.GetRepoClustersAsync();
        var projectCluster = allClusters.ToDictionary(c => c.ProjectName, c => c.ClusterId, StringComparer.OrdinalIgnoreCase);

        var memberSet = members.Select(m => m.ProjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Per-member edge counts
        var memberDetails = new List<ClusterMember>();
        foreach (var m in members)
        {
            int internalEdges = allEdges.Count(e =>
                (e.SourceProject.Equals(m.ProjectName, StringComparison.OrdinalIgnoreCase) && memberSet.Contains(e.TargetProject)) ||
                (e.TargetProject.Equals(m.ProjectName, StringComparison.OrdinalIgnoreCase) && memberSet.Contains(e.SourceProject)));
            int externalEdges = allEdges.Count(e =>
                (e.SourceProject.Equals(m.ProjectName, StringComparison.OrdinalIgnoreCase) && !memberSet.Contains(e.TargetProject)) ||
                (e.TargetProject.Equals(m.ProjectName, StringComparison.OrdinalIgnoreCase) && !memberSet.Contains(e.SourceProject)));

            memberDetails.Add(new ClusterMember(m.ProjectName, m.BetweennessCentrality, internalEdges, externalEdges));
        }

        // Cross-cluster connections
        var crossClusterEdges = allEdges.Where(e =>
            (memberSet.Contains(e.SourceProject) && !memberSet.Contains(e.TargetProject)) ||
            (memberSet.Contains(e.TargetProject) && !memberSet.Contains(e.SourceProject)));

        var connections = crossClusterEdges
            .Select(e => memberSet.Contains(e.SourceProject) ? e.TargetProject : e.SourceProject)
            .Where(p => projectCluster.ContainsKey(p))
            .GroupBy(p => projectCluster[p])
            .Select(g =>
            {
                var targetClusterMembers = allClusters.Where(c => c.ClusterId == g.Key).ToList();
                return new ClusterConnection(
                    g.Key,
                    targetClusterMembers.FirstOrDefault()?.ClusterLabel,
                    g.Count(),
                    crossClusterEdges
                        .Where(e =>
                            (memberSet.Contains(e.SourceProject) && projectCluster.GetValueOrDefault(e.TargetProject, -1) == g.Key) ||
                            (memberSet.Contains(e.TargetProject) && projectCluster.GetValueOrDefault(e.SourceProject, -1) == g.Key))
                        .Select(e => e.Type.ToString())
                        .Distinct()
                        .ToList());
            })
            .OrderByDescending(c => c.EdgeCount)
            .ToList();

        int totalInternal = allEdges.Count(e => memberSet.Contains(e.SourceProject) && memberSet.Contains(e.TargetProject));
        int totalExternal = crossClusterEdges.Count();

        return new ClusterDetailResponse(
            clusterId,
            members.First().ClusterLabel,
            memberDetails,
            totalInternal,
            totalExternal,
            connections);
    }

    public async Task<ClusterGraphResponse> GetClusterGraphAsync()
    {
        var repos = await store.ListRepositoriesAsync();
        var allEdges = await store.GetAllCrossRepoEdgesAsync();
        var clusters = await store.GetRepoClustersAsync();

        var projectCluster = clusters.GroupBy(c => c.ProjectName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ClusterId, StringComparer.OrdinalIgnoreCase);
        var projectBetweenness = clusters.GroupBy(c => c.ProjectName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().BetweennessCentrality, StringComparer.OrdinalIgnoreCase);

        var nodes = repos.Select(r => new ClusterGraphNode(
            r.Name,
            r.SourceGroup,
            r.Language,
            r.Framework,
            r.IsFoundational,
            projectCluster.GetValueOrDefault(r.Name),
            projectBetweenness.GetValueOrDefault(r.Name, 0))).ToList();

        var edges = allEdges
            .GroupBy(e => (Source: e.SourceProject, Target: e.TargetProject))
            .Select(g =>
            {
                var sourceCluster = projectCluster.GetValueOrDefault(g.Key.Source, -1);
                var targetCluster = projectCluster.GetValueOrDefault(g.Key.Target, -2);
                return new ClusterGraphEdge(
                    g.Key.Source,
                    g.Key.Target,
                    g.Count(),
                    g.GroupBy(e => e.Type.ToString()).ToDictionary(tg => tg.Key, tg => tg.Count()),
                    sourceCluster != targetCluster);
            })
            .ToList();

        var clusterInfos = clusters
            .GroupBy(c => c.ClusterId)
            .Select(g => new ClusterInfo(g.Key, g.First().ClusterLabel, g.Count()))
            .OrderBy(c => c.ClusterId)
            .ToList();

        double modularity = clusters.Count > 0 ? (double)clusters.First().ModularityScore : 0;

        return new ClusterGraphResponse(nodes, edges, clusterInfos, modularity);
    }

    private static Dictionary<string, Dictionary<string, double>> BuildWeightedAdjacency(
        IReadOnlyList<CrossRepoEdge> edges)
    {
        var adjacency = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        // Accumulate directed weights with case-insensitive keys
        var directedWeights = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            double weight = EdgeWeights.GetValueOrDefault(edge.Type.ToString(), DefaultWeight);

            if (!directedWeights.TryGetValue(edge.SourceProject, out var targets))
            {
                targets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                directedWeights[edge.SourceProject] = targets;
            }
            targets[edge.TargetProject] = targets.GetValueOrDefault(edge.TargetProject, 0) + weight;
        }

        // Convert to undirected with bidirectional bonus
        var processedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, targets) in directedWeights)
        {
            foreach (var (target, weight) in targets)
            {
                // Normalize pair order for dedup
                var (a, b) = string.Compare(source, target, StringComparison.OrdinalIgnoreCase) <= 0
                    ? (source, target)
                    : (target, source);
                var pairKey = $"{a}|{b}";

                if (processedPairs.Contains(pairKey)) continue;
                processedPairs.Add(pairKey);

                double forwardWeight = weight;
                double reverseWeight = directedWeights.GetValueOrDefault(target)?.GetValueOrDefault(source, 0) ?? 0;
                double totalWeight = forwardWeight + reverseWeight;

                // Apply bidirectional bonus if edges exist in both directions
                if (forwardWeight > 0 && reverseWeight > 0)
                    totalWeight *= BidirectionalBonus;

                if (!adjacency.ContainsKey(a))
                    adjacency[a] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (!adjacency.ContainsKey(b))
                    adjacency[b] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                adjacency[a][b] = totalWeight;
                adjacency[b][a] = totalWeight;
            }
        }

        return adjacency;
    }
}
