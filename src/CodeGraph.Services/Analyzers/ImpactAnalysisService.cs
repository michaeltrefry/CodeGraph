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

    private const string LowConfidenceFactor =
        "Low-confidence node (tree-sitter fallback; relationships may be incomplete)";

    // Node labels that indicate test code — demote severity
    private static readonly HashSet<NodeLabel> TestLabels = new()
    {
        NodeLabel.File // we check file path for "test" patterns
    };

    public async Task<ImpactReport?> AnalyzeImpactAsync(
        string qualifiedName,
        string? project = null,
        int maxDepth = 3,
        IGraphReader? graph = null)
    {
        graph ??= store;
        var startNodes = await ResolveNodesAsync(graph, qualifiedName, project);
        if (startNodes.Count == 0)
            return null;

        return await BuildImpactReportAsync(graph, startNodes, maxDepth);
    }

    public async Task<ImpactReport?> AnalyzeFileImpactAsync(
        string project,
        string filePath,
        int maxDepth = 3,
        IGraphReader? graph = null)
    {
        graph ??= store;
        var fileNodes = await graph.FindNodesByFileAsync(project, filePath);
        if (fileNodes.Count == 0)
            return null;

        // Filter to meaningful nodes (classes, methods, interfaces — not files/folders)
        var meaningfulNodes = fileNodes.Where(n =>
            n.Label is not (NodeLabel.File or NodeLabel.Folder or NodeLabel.Namespace)).ToList();

        if (meaningfulNodes.Count == 0)
            meaningfulNodes = fileNodes.ToList();

        var report = await BuildImpactReportAsync(graph, meaningfulNodes, maxDepth);

        // Repositories that depend on the file's crate, which symbol edges alone do not show.
        var crateImpacts = (await FindCrateDependentReposAsync(graph, project, filePath, maxDepth))
            .Where(crate => !report.CrossRepoImpacts.Any(existing =>
                (existing.SourceProject == crate.SourceProject && existing.TargetProject == crate.TargetProject) ||
                (existing.SourceProject == crate.TargetProject && existing.TargetProject == crate.SourceProject)))
            .ToList();
        if (crateImpacts.Count == 0)
            return report;

        var crossRepoImpacts = report.CrossRepoImpacts.Concat(crateImpacts).ToList();
        return report with
        {
            CrossRepoImpacts = crossRepoImpacts,
            Summary = report.Summary with { CrossRepoCount = CountCrossRepoProjects(crossRepoImpacts, new HashSet<string> { project }) }
        };
    }

    /// <summary>
    /// Cross-repo Cargo/SwiftPM package dependencies on the package that owns a Rust or Swift file: other repositories
    /// that reference the crate, directly or through crates that depend on it, within
    /// <paramref name="maxDepth"/> package hops. These are crate-level dependents — they are not
    /// affected nodes and do not change the risk counts.
    /// </summary>
    private static async Task<IReadOnlyList<CrossRepoImpact>> FindCrateDependentReposAsync(
        IGraphReader graph, string project, string filePath, int maxDepth)
    {
        var crate = await FindOwningCrateAsync(graph, project, filePath);
        if (crate is null)
            return [];

        var visited = new HashSet<long> { crate.Id };
        var frontier = new HashSet<long> { crate.Id };
        var crossRepoEdges = new Dictionary<string, IReadOnlyList<CrossRepoEdge>>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<string> { project };
        var dependents = new Dictionary<(long Source, long Target), CrossRepoEdge>();

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new HashSet<long>();
            foreach (var edge in await graph.FindEdgesByTargetBatchAsync(
                         frontier.ToList(), [EdgeType.REFERENCES_PACKAGE]))
            {
                if (visited.Add(edge.SourceId))
                    next.Add(edge.SourceId);
            }

            foreach (var edgeProject in projects.ToList())
            {
                if (!crossRepoEdges.TryGetValue(edgeProject, out var edges))
                {
                    edges = await graph.FindCrossRepoEdgesAsync(edgeProject, EdgeType.REFERENCES_PACKAGE);
                    crossRepoEdges[edgeProject] = edges;
                }

                foreach (var edge in edges.Where(edge => frontier.Contains(edge.TargetNodeId)))
                {
                    dependents.TryAdd((edge.SourceNodeId, edge.TargetNodeId), edge);
                    if (visited.Add(edge.SourceNodeId))
                        next.Add(edge.SourceNodeId);
                    if (!projects.Contains(edge.SourceProject, StringComparer.OrdinalIgnoreCase))
                        projects.Add(edge.SourceProject);
                }
            }

            frontier = next;
        }

        return dependents.Values
            .GroupBy(edge => (edge.SourceProject, edge.TargetProject))
            .Select(group => new CrossRepoImpact(
                group.Key.SourceProject, group.Key.TargetProject,
                EdgeType.REFERENCES_PACKAGE.ToString(), group.Count()))
            .ToList();
    }

    /// <summary>
    /// The package defined by the nearest manifest above a source file: <c>Cargo.toml</c> for a
    /// <c>.rs</c> file, <c>Package.swift</c> for a <c>.swift</c> file.
    /// </summary>
    private static async Task<GraphNode?> FindOwningCrateAsync(IGraphReader graph, string project, string filePath)
    {
        string manifestName;
        if (filePath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
            manifestName = "Cargo.toml";
        else if (filePath.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
            manifestName = "Package.swift";
        else
            return null;

        var directory = filePath.Replace('\\', '/');
        while (directory.Length > 0)
        {
            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
            var manifest = directory.Length == 0 ? manifestName : $"{directory}/{manifestName}";
            var definition = (await graph.FindNodesByFileAsync(project, manifest))
                .FirstOrDefault(node => node.Label == NodeLabel.Package && IsDefinition(node));
            if (definition is not null)
                return definition;
        }

        return null;
    }

    private static bool IsDefinition(GraphNode node) =>
        node.Properties is not null &&
        node.Properties.TryGetValue("is_definition", out var value) &&
        value switch
        {
            bool flag => flag,
            string text => bool.TryParse(text, out var parsed) && parsed,
            System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.True,
            _ => false
        };

    private static async Task<IReadOnlyList<GraphNode>> ResolveNodesAsync(IGraphReader graph, string qualifiedName, string? project)
    {
        // Try exact qualified name first
        if (project is not null)
        {
            var exact = await graph.FindNodeByQualifiedNameAsync(project, qualifiedName);
            if (exact is not null)
                return [exact];
        }

        // Fall back to name search
        var candidates = project is not null
            ? await graph.FindNodesByNameAsync(project, qualifiedName)
            : await graph.SearchNodesAsync(null, qualifiedName, limit: 10);

        if (candidates.Count == 0)
            return [];

        // Prefer code elements over structural nodes
        var preferred = candidates.Where(n =>
            n.Label is NodeLabel.Method or NodeLabel.Function or NodeLabel.Class
                or NodeLabel.Interface or NodeLabel.Service or NodeLabel.Event
                or NodeLabel.Route).ToList();

        return preferred.Count > 0 ? preferred : [candidates[0]];
    }

    private async Task<ImpactReport> BuildImpactReportAsync(IGraphReader graph, IReadOnlyList<GraphNode> startNodes, int maxDepth)
    {
        // BFS results: nodeId → (depth, edgeType, sourceProject)
        var visited = new Dictionary<long, (int Depth, EdgeType EdgeType, string SourceProject)>();
        var reachedProjects = new HashSet<string>(startNodes.Select(n => n.Project));
        foreach (var node in startNodes)
            visited[node.Id] = (0, default, node.Project);

        // Phase 1: Intra-project BFS from each start node
        foreach (var startNode in startNodes)
        {
            var entries = await graph.TraverseAsync(
                startNode.Id, TraceDirection.Inbound, maxDepth, DependencyEdges);

            foreach (var entry in entries)
            {
                if (visited.TryGetValue(entry.Node.Id, out var existing) && existing.Depth <= entry.Depth)
                    continue;
                visited[entry.Node.Id] = (entry.Depth, entry.EdgeType, startNode.Project);
                reachedProjects.Add(entry.Node.Project);
            }
        }

        // Phase 2: Follow cross-repo edges and continue BFS into other projects.
        // Each wave fetches cross-repo edges for every project reached so far that has not
        // been fetched yet, seeds the dependents across those edges and traverses inbound
        // from them; the projects those dependents live in are fetched in the next wave.
        var fetchedProjects = new HashSet<string>();
        var crossRepoEdges = new List<CrossRepoEdge>();
        var seenEdges = new HashSet<(long, long, EdgeType)>();

        for (int wave = 0; wave < maxDepth; wave++)
        {
            foreach (var project in reachedProjects.Where(fetchedProjects.Add).ToList())
            {
                // An edge between two fetched projects is returned for both of them.
                foreach (var edge in await graph.FindCrossRepoEdgesAsync(project))
                {
                    if (seenEdges.Add((edge.SourceNodeId, edge.TargetNodeId, edge.Type)))
                        crossRepoEdges.Add(edge);
                }
            }

            // Seed the dependent end of every fetched edge whose depended-upon end is visited.
            // Every fetched edge is checked each wave: a node visited in the last wave may sit
            // at the depended-upon end of an edge fetched earlier, and a later wave may find a
            // shorter path to a node that is already visited.
            var crossRepoSeeds = new List<(long nodeId, string nodeProject, int baseDepth, EdgeType edgeType, string sourceProject)>();
            foreach (var edge in crossRepoEdges)
            {
                // Cross-repo edges written by CrossRepoLinker point from the dependent to what it
                // depends on (HTTP_CALLS, REFERENCES_PACKAGE, CALLS/USES_TYPE/IMPLEMENTS) except
                // PUBLISHES, which runs publisher → consumer. Only the depended-upon end's
                // changes affect the other end.
                var (dependency, dependencyProject, dependent, dependentProject) = edge.Type == EdgeType.PUBLISHES
                    ? (edge.SourceNodeId, edge.SourceProject, edge.TargetNodeId, edge.TargetProject)
                    : (edge.TargetNodeId, edge.TargetProject, edge.SourceNodeId, edge.SourceProject);
                if (!visited.TryGetValue(dependency, out var reached))
                    continue;

                var baseDepth = reached.Depth + 1;
                if (baseDepth <= maxDepth &&
                    (!visited.TryGetValue(dependent, out var current) || current.Depth > baseDepth))
                    crossRepoSeeds.Add((dependent, dependentProject, baseDepth, edge.Type, dependencyProject));
            }

            if (crossRepoSeeds.Count == 0)
                break;

            var addedAny = false;
            foreach (var (nodeId, nodeProject, baseDepth, edgeType, sourceProject) in crossRepoSeeds)
            {
                if (visited.TryGetValue(nodeId, out var existing) && existing.Depth <= baseDepth)
                    continue;

                visited[nodeId] = (baseDepth, edgeType, sourceProject);
                reachedProjects.Add(nodeProject);
                addedAny = true;

                // Continue BFS inbound from this cross-repo node
                var remainingDepth = maxDepth - baseDepth;
                if (remainingDepth > 0)
                {
                    var entries = await graph.TraverseAsync(
                        nodeId, TraceDirection.Inbound, remainingDepth, DependencyEdges);

                    foreach (var entry in entries)
                    {
                        var adjustedDepth = baseDepth + entry.Depth;
                        if (adjustedDepth > maxDepth) continue;
                        if (visited.TryGetValue(entry.Node.Id, out var ex) && ex.Depth <= adjustedDepth)
                            continue;
                        visited[entry.Node.Id] = (adjustedDepth, entry.EdgeType, sourceProject);
                        reachedProjects.Add(entry.Node.Project);
                    }
                }
            }

            if (!addedAny)
                break;
        }

        var startNodeIds = new HashSet<long>(startNodes.Select(n => n.Id));

        // Build affected nodes with risk classification
        var affectedNodes = new List<AffectedNode>();
        var changedNodes = new List<AffectedNode>();
        var affectedProjectSet = new HashSet<string>();

        // We need to fetch node details for entries beyond start nodes
        var allNodeIds = visited.Keys.Except(startNodeIds).ToList();
        var nodeMap = await graph.FindNodesByIdBatchAsync(allNodeIds);

        // Add start nodes as "changed" nodes
        foreach (var node in startNodes)
        {
            changedNodes.Add(new AffectedNode(
                node.Id, node.Name, node.QualifiedName, node.Label.ToString(),
                node.Project, node.DotnetProject, node.FilePath,
                Depth: 0, EdgeType: "CHANGED", Risk: RiskLevel.Critical,
                RiskFactors: GraphNodeConfidence.IsLowConfidence(node)
                    ? ["Changed node", LowConfidenceFactor]
                    : ["Changed node"],
                LowConfidence: GraphNodeConfidence.IsLowConfidence(node)));
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
            var lowConfidence = GraphNodeConfidence.IsLowConfidence(node);
            if (lowConfidence)
                factors = [.. factors, LowConfidenceFactor];

            affectedNodes.Add(new AffectedNode(
                node.Id, node.Name, node.QualifiedName, node.Label.ToString(),
                node.Project, node.DotnetProject, node.FilePath,
                depth, edgeType.ToString(), risk, factors, lowConfidence));
        }

        // Build cross-repo impact summary from edges whose depended-upon end is a start node
        var crossRepoImpacts = crossRepoEdges
            .Where(e => startNodeIds.Contains(e.Type == EdgeType.PUBLISHES ? e.SourceNodeId : e.TargetNodeId))
            .GroupBy(e => new { e.SourceProject, e.TargetProject, e.Type })
            .Select(g => new CrossRepoImpact(
                g.Key.SourceProject, g.Key.TargetProject,
                g.Key.Type.ToString(), g.Count()))
            .ToList();

        // Projects reached only through traversal get a TRANSITIVE row; a project with a
        // direct edge to a start node is already counted above.
        var directDependentProjects = crossRepoImpacts
            .Select(c => c.EdgeType == nameof(EdgeType.PUBLISHES) ? c.TargetProject : c.SourceProject)
            .ToHashSet();
        var traversalCrossRepo = affectedNodes
            .Where(n => !startNodes.Any(s => s.Project == n.Project) && !directDependentProjects.Contains(n.Project))
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
            CrossRepoCount: CountCrossRepoProjects(allCrossRepo, startNodes.Select(n => n.Project).ToHashSet()),
            CriticalCount: affectedNodes.Count(n => n.Risk == RiskLevel.Critical),
            HighCount: affectedNodes.Count(n => n.Risk == RiskLevel.High),
            MediumCount: affectedNodes.Count(n => n.Risk == RiskLevel.Medium),
            LowCount: affectedNodes.Count(n => n.Risk == RiskLevel.Low),
            AffectedProjects: affectedProjectSet.Order().ToList(),
            LowConfidenceCount: changedNodes.Count(n => n.LowConfidence) +
                                affectedNodes.Count(n => n.LowConfidence));

        logger.LogInformation(
            "Impact analysis for {NodeCount} nodes: {Affected} affected, {CrossRepo} cross-repo",
            startNodes.Count, affectedNodes.Count, summary.CrossRepoCount);

        return new ImpactReport(changedNodes, affectedNodes, allCrossRepo, summary);
    }

    // One dependent project can have several rows (e.g. it calls the change and consumes its events).
    private static int CountCrossRepoProjects(IEnumerable<CrossRepoImpact> impacts, IReadOnlySet<string> startProjects) =>
        impacts.SelectMany(c => new[] { c.SourceProject, c.TargetProject })
            .Where(p => !startProjects.Contains(p))
            .Distinct()
            .Count();

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
