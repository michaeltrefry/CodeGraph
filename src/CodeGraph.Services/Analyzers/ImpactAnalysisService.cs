using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public class ImpactAnalysisService(
    IGraphStore store,
    ILogger<ImpactAnalysisService> logger) : IImpactAnalysisService
{
    // Edge types that represent "who depends on me" — follow inbound
    private static readonly EdgeType[] DependencyEdges =
    [
        EdgeType.CALLS,
        EdgeType.HTTP_CALLS,
        EdgeType.CONSUMES,
        EdgeType.USES_TYPE,
        EdgeType.INJECTS,
        EdgeType.IMPLEMENTS,
        EdgeType.INHERITS,
        EdgeType.HANDLES,
        EdgeType.REFERENCES_PACKAGE,
        EdgeType.RENDERS,
        EdgeType.SUBSCRIBES
    ];

    // Cross-repo edge types that boost severity
    private static readonly HashSet<EdgeType> CrossRepoEdgeTypes = new()
    {
        EdgeType.HTTP_CALLS,
        EdgeType.CONSUMES,
        EdgeType.PUBLISHES,
        EdgeType.REFERENCES_PACKAGE
    };

    // Async/messaging edges — silent failures boost severity
    private static readonly HashSet<EdgeType> AsyncEdgeTypes = new()
    {
        EdgeType.CONSUMES,
        EdgeType.PUBLISHES
    };

    // Node labels that indicate test code — demote severity
    private static readonly HashSet<NodeLabel> TestLabels = new()
    {
        NodeLabel.File // we check file path for "test" patterns
    };

    public async Task<ImpactReport?> AnalyzeImpactAsync(string qualifiedName, string? project = null, int maxDepth = 3)
    {
        var startNodes = await ResolveNodesAsync(qualifiedName, project);
        if (startNodes.Count == 0)
            return null;

        return await BuildImpactReportAsync(startNodes, maxDepth);
    }

    public async Task<ImpactReport?> AnalyzeFileImpactAsync(string project, string filePath, int maxDepth = 3)
    {
        var fileNodes = await store.FindNodesByFileAsync(project, filePath);
        if (fileNodes.Count == 0)
            return null;

        // Filter to meaningful nodes (classes, methods, interfaces — not files/folders)
        var meaningfulNodes = fileNodes.Where(n =>
            n.Label is not (NodeLabel.File or NodeLabel.Folder or NodeLabel.Namespace)).ToList();

        if (meaningfulNodes.Count == 0)
            meaningfulNodes = fileNodes.ToList();

        return await BuildImpactReportAsync(meaningfulNodes, maxDepth);
    }

    private async Task<IReadOnlyList<GraphNode>> ResolveNodesAsync(string qualifiedName, string? project)
    {
        // Try exact qualified name first
        if (project is not null)
        {
            var exact = await store.FindNodeByQualifiedNameAsync(project, qualifiedName);
            if (exact is not null)
                return [exact];
        }

        // Fall back to name search
        var candidates = project is not null
            ? await store.FindNodesByNameAsync(project, qualifiedName)
            : await store.SearchNodesAsync(null, qualifiedName, limit: 10);

        if (candidates.Count == 0)
            return [];

        // Prefer code elements over structural nodes
        var preferred = candidates.Where(n =>
            n.Label is NodeLabel.Method or NodeLabel.Function or NodeLabel.Class
                or NodeLabel.Interface or NodeLabel.Service or NodeLabel.Event
                or NodeLabel.Route).ToList();

        return preferred.Count > 0 ? preferred : [candidates[0]];
    }

    private async Task<ImpactReport> BuildImpactReportAsync(IReadOnlyList<GraphNode> startNodes, int maxDepth)
    {
        // BFS results: nodeId → (depth, edgeType, sourceProject)
        var visited = new Dictionary<long, (int Depth, EdgeType EdgeType, string SourceProject)>();
        foreach (var node in startNodes)
            visited[node.Id] = (0, default, node.Project);

        // Phase 1: Intra-project BFS from each start node
        foreach (var startNode in startNodes)
        {
            var entries = await store.TraverseAsync(
                startNode.Id, TraceDirection.Inbound, maxDepth, DependencyEdges);

            foreach (var entry in entries)
            {
                if (visited.TryGetValue(entry.Node.Id, out var existing) && existing.Depth <= entry.Depth)
                    continue;
                visited[entry.Node.Id] = (entry.Depth, entry.EdgeType, startNode.Project);
            }
        }

        // Phase 2: Follow cross-repo edges and continue BFS into other projects
        // We iterate in waves — each wave finds cross-repo edges from visited nodes,
        // then traverses inbound within the target projects.
        var checkedProjects = new HashSet<string>(startNodes.Select(n => n.Project));
        var crossRepoEdges = new List<CrossRepoEdge>();
        var frontier = new HashSet<long>(visited.Keys);

        for (int wave = 0; wave < maxDepth; wave++)
        {
            // Find cross-repo edges touching any visited node
            var newProjects = new HashSet<string>();
            foreach (var project in checkedProjects.ToList())
            {
                // Only fetch cross-repo edges for projects we haven't checked yet
            }

            // Fetch cross-repo edges for all projects that have visited nodes
            var projectsToCheck = visited.Values
                .Select(v => v.SourceProject)
                .Concat(startNodes.Select(n => n.Project))
                .Distinct()
                .Where(p => checkedProjects.Add(p) || wave == 0)
                .ToList();

            // On first wave, re-check start projects; on later waves only new ones
            if (wave == 0)
                projectsToCheck = checkedProjects.ToList();

            var waveEdges = new List<CrossRepoEdge>();
            foreach (var project in projectsToCheck)
            {
                var edges = await store.FindCrossRepoEdgesAsync(project);
                crossRepoEdges.AddRange(edges);
                waveEdges.AddRange(edges);
            }

            // Find cross-repo edges where one end is a visited node (inbound direction:
            // the visited node is the target, so dependents are in the source project)
            var crossRepoSeeds = new List<(long nodeId, int baseDepth, EdgeType edgeType, string sourceProject)>();
            foreach (var edge in waveEdges)
            {
                // If a visited node is referenced as a target (someone depends on it),
                // the source node in another project is affected
                if (visited.ContainsKey(edge.TargetNodeId) && !visited.ContainsKey(edge.SourceNodeId))
                {
                    var baseDepth = visited[edge.TargetNodeId].Depth + 1;
                    if (baseDepth <= maxDepth)
                        crossRepoSeeds.Add((edge.SourceNodeId, baseDepth, edge.Type, edge.TargetProject));
                }
                // Also: if a visited node is the source (it depends on something),
                // the target node's dependents in other projects are affected
                if (visited.ContainsKey(edge.SourceNodeId) && !visited.ContainsKey(edge.TargetNodeId))
                {
                    var baseDepth = visited[edge.SourceNodeId].Depth + 1;
                    if (baseDepth <= maxDepth)
                        crossRepoSeeds.Add((edge.TargetNodeId, baseDepth, edge.Type, edge.SourceProject));
                }
            }

            if (crossRepoSeeds.Count == 0)
                break;

            var addedAny = false;
            foreach (var (nodeId, baseDepth, edgeType, sourceProject) in crossRepoSeeds)
            {
                if (visited.TryGetValue(nodeId, out var existing) && existing.Depth <= baseDepth)
                    continue;

                visited[nodeId] = (baseDepth, edgeType, sourceProject);
                addedAny = true;

                // Continue BFS inbound from this cross-repo node
                var remainingDepth = maxDepth - baseDepth;
                if (remainingDepth > 0)
                {
                    var entries = await store.TraverseAsync(
                        nodeId, TraceDirection.Inbound, remainingDepth, DependencyEdges);

                    foreach (var entry in entries)
                    {
                        var adjustedDepth = baseDepth + entry.Depth;
                        if (adjustedDepth > maxDepth) continue;
                        if (visited.TryGetValue(entry.Node.Id, out var ex) && ex.Depth <= adjustedDepth)
                            continue;
                        visited[entry.Node.Id] = (adjustedDepth, entry.EdgeType, sourceProject);
                    }
                }
            }

            if (!addedAny)
                break;

            // Update checked projects for the next wave
            foreach (var (_, (_, _, proj)) in visited)
                checkedProjects.Add(proj);
        }

        var startNodeIds = new HashSet<long>(startNodes.Select(n => n.Id));

        // Build affected nodes with risk classification
        var affectedNodes = new List<AffectedNode>();
        var changedNodes = new List<AffectedNode>();
        var affectedProjectSet = new HashSet<string>();

        // We need to fetch node details for entries beyond start nodes
        var allNodeIds = visited.Keys.Except(startNodeIds).ToList();
        var nodeMap = await store.FindNodesByIdBatchAsync(allNodeIds);

        // Add start nodes as "changed" nodes
        foreach (var node in startNodes)
        {
            changedNodes.Add(new AffectedNode(
                node.Id, node.Name, node.QualifiedName, node.Label.ToString(),
                node.Project, node.DotnetProject, node.FilePath,
                Depth: 0, EdgeType: "CHANGED", Risk: RiskLevel.Critical,
                RiskFactors: ["Changed node"]));
            affectedProjectSet.Add(node.Project);
        }

        // Classify each affected node
        foreach (var (nodeId, (depth, edgeType, sourceProject)) in visited)
        {
            if (startNodeIds.Contains(nodeId))
                continue;

            if (!nodeMap.TryGetValue(nodeId, out var node))
                continue;

            var (risk, factors) = ClassifyRisk(node, depth, edgeType, sourceProject);
            affectedProjectSet.Add(node.Project);

            affectedNodes.Add(new AffectedNode(
                node.Id, node.Name, node.QualifiedName, node.Label.ToString(),
                node.Project, node.DotnetProject, node.FilePath,
                depth, edgeType.ToString(), risk, factors));
        }

        // Build cross-repo impact summary
        var crossRepoImpacts = crossRepoEdges
            .Where(e => startNodeIds.Contains(e.SourceNodeId) || startNodeIds.Contains(e.TargetNodeId))
            .GroupBy(e => new { e.SourceProject, e.TargetProject, e.Type })
            .Select(g => new CrossRepoImpact(
                g.Key.SourceProject, g.Key.TargetProject,
                g.Key.Type.ToString(), g.Count()))
            .ToList();

        // Also count cross-repo from traversal results
        var traversalCrossRepo = affectedNodes
            .Where(n => !startNodes.Any(s => s.Project == n.Project))
            .GroupBy(n => n.Project)
            .Select(g =>
            {
                var startProject = startNodes[0].Project;
                return new CrossRepoImpact(
                    startProject, g.Key,
                    "TRANSITIVE", g.Count());
            })
            .ToList();

        var allCrossRepo = crossRepoImpacts.Concat(traversalCrossRepo)
            .GroupBy(c => new { c.SourceProject, c.TargetProject })
            .Select(g => g.First() with { AffectedNodeCount = g.Sum(x => x.AffectedNodeCount) })
            .ToList();

        var summary = new ImpactSummary(
            TotalAffected: affectedNodes.Count,
            CrossRepoCount: allCrossRepo.Count,
            CriticalCount: affectedNodes.Count(n => n.Risk == RiskLevel.Critical),
            HighCount: affectedNodes.Count(n => n.Risk == RiskLevel.High),
            MediumCount: affectedNodes.Count(n => n.Risk == RiskLevel.Medium),
            LowCount: affectedNodes.Count(n => n.Risk == RiskLevel.Low),
            AffectedProjects: affectedProjectSet.Order().ToList());

        logger.LogInformation(
            "Impact analysis for {NodeCount} nodes: {Affected} affected, {CrossRepo} cross-repo",
            startNodes.Count, affectedNodes.Count, allCrossRepo.Count);

        return new ImpactReport(changedNodes, affectedNodes, allCrossRepo, summary);
    }

    private static (RiskLevel Risk, IReadOnlyList<string> Factors) ClassifyRisk(
        GraphNode node, int depth, EdgeType edgeType, string sourceProject)
    {
        var factors = new List<string>();

        // Base risk from hop distance
        var baseRisk = depth switch
        {
            1 => RiskLevel.High,
            2 => RiskLevel.Medium,
            _ => RiskLevel.Low
        };

        var riskScore = (int)baseRisk; // 0=Critical, 1=High, 2=Medium, 3=Low

        // Boosting: cross-repo edge
        if (node.Project != sourceProject)
        {
            riskScore--;
            factors.Add("Cross-repo dependency (harder to detect breakage)");
        }

        // Boosting: async/messaging edge
        if (AsyncEdgeTypes.Contains(edgeType))
        {
            riskScore--;
            factors.Add($"Async edge ({edgeType}) — silent failures");
        }

        // Boosting: direct consumer of cross-repo event at hop 1
        if (depth == 1 && CrossRepoEdgeTypes.Contains(edgeType) && node.Project != sourceProject)
        {
            riskScore = 0; // Force Critical
            factors.Add("Direct cross-repo consumer");
        }

        // Demoting: test code
        if (IsTestNode(node))
        {
            riskScore++;
            factors.Add("Test code (caught by CI)");
        }

        // Clamp to valid range
        var risk = riskScore switch
        {
            <= 0 => RiskLevel.Critical,
            1 => RiskLevel.High,
            2 => RiskLevel.Medium,
            _ => RiskLevel.Low
        };

        if (factors.Count == 0)
            factors.Add($"Hop {depth} dependent");

        return (risk, factors);
    }

    private static bool IsTestNode(GraphNode node)
    {
        if (string.IsNullOrEmpty(node.FilePath))
            return false;

        var path = node.FilePath.ToLowerInvariant();
        return path.Contains("test") || path.Contains("spec") ||
               node.DotnetProject?.Contains("Test", StringComparison.OrdinalIgnoreCase) == true;
    }
}
