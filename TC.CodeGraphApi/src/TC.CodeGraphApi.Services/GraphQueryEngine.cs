using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services;

public class GraphQueryEngine
{
    private readonly IGraphStore _store;
    private readonly ILogger<GraphQueryEngine> _logger;

    public GraphQueryEngine(IGraphStore store, ILogger<GraphQueryEngine> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Search for nodes by name pattern, label, project.
    /// </summary>
    public async Task<SearchResult> SearchAsync(SearchRequest request)
    {
        var nodes = await _store.SearchNodesAsync(
            request.Project,
            request.NamePattern,
            request.Label,
            limit: request.Limit);

        return new SearchResult(nodes, nodes.Count);
    }

    /// <summary>
    /// Trace call path (callers/callees) from a function.
    /// </summary>
    public async Task<IReadOnlyList<TraversalEntry>> TraceCallPathAsync(
        string functionName, string? project, TraceDirection direction,
        int maxDepth = 3)
    {
        var candidates = project is not null
            ? await _store.FindNodesByNameAsync(project, functionName)
            : await _store.SearchNodesAsync(null, functionName, limit: 10);

        if (candidates.Count == 0)
            return [];

        // Prefer Method/Function nodes, fall back to first match
        var startNode = candidates.FirstOrDefault(n =>
            n.Label is NodeLabel.Method or NodeLabel.Function) ?? candidates[0];

        var callEdges = new[] { EdgeType.CALLS, EdgeType.HTTP_CALLS };
        return await _store.TraverseAsync(startNode.Id, direction, maxDepth, callEdges);
    }

    /// <summary>
    /// Trace data lineage: follow a model from origin through all services.
    /// </summary>
    public async Task<DataLineageResult> TraceDataLineageAsync(
        string modelName, string? project)
    {
        var candidates = project is not null
            ? await _store.FindNodesByNameAsync(project, modelName)
            : await _store.SearchNodesAsync(null, modelName, limit: 10);

        if (candidates.Count == 0)
            return new DataLineageResult(modelName, [], [], []);

        // Prefer Class/Record/Interface nodes (DTOs/events)
        var modelNode = candidates.FirstOrDefault(n =>
            n.Label is NodeLabel.Class or NodeLabel.Record or NodeLabel.Interface)
            ?? candidates[0];

        // Trace outbound: who consumes/uses this model
        var dataEdges = new[] { EdgeType.USES_TYPE, EdgeType.PUBLISHES, EdgeType.CONSUMES, EdgeType.HTTP_CALLS };
        var outbound = await _store.TraverseAsync(modelNode.Id, TraceDirection.Outbound, 4, dataEdges);

        // Trace inbound: who produces this model
        var inbound = await _store.TraverseAsync(modelNode.Id, TraceDirection.Inbound, 4, dataEdges);

        // Cross-repo edges involving this model's project
        var crossRepo = modelNode.Project is not null
            ? await _store.FindCrossRepoEdgesAsync(modelNode.Project)
            : [];

        return new DataLineageResult(modelName, inbound, outbound, crossRepo);
    }

    /// <summary>
    /// Find all consumers of an event/endpoint/model.
    /// </summary>
    public async Task<IReadOnlyList<ConsumerInfo>> FindConsumersAsync(
        string name, string? project)
    {
        var candidates = project is not null
            ? await _store.FindNodesByNameAsync(project, name)
            : await _store.SearchNodesAsync(null, name, limit: 10);

        if (candidates.Count == 0)
            return [];

        var results = new List<ConsumerInfo>();

        foreach (var node in candidates)
        {
            // Find edges where this node is the target (consumers)
            var consumeEdges = await _store.FindEdgesByTargetAsync(node.Id);
            foreach (var edge in consumeEdges.Where(e =>
                e.Type is EdgeType.CONSUMES or EdgeType.CALLS or EdgeType.HTTP_CALLS
                    or EdgeType.USES_TYPE or EdgeType.HANDLES))
            {
                // Resolve the source node
                var sourceNodes = await _store.SearchNodesAsync(null, "%", limit: 1);
                // We need to find the source by ID — use traversal with depth 1
                var path = await _store.TraverseAsync(node.Id, TraceDirection.Inbound, 1,
                    [edge.Type]);
                foreach (var entry in path)
                {
                    results.Add(new ConsumerInfo(
                        entry.Node.Name,
                        entry.Node.Project,
                        entry.Node.QualifiedName,
                        edge.Type));
                }
            }
        }

        return results.DistinctBy(c => c.QualifiedName).ToList();
    }

    /// <summary>
    /// Find all publishers to a queue/exchange.
    /// </summary>
    public async Task<IReadOnlyList<PublisherInfo>> FindPublishersAsync(
        string name, string? project)
    {
        var candidates = project is not null
            ? await _store.FindNodesByNameAsync(project, name)
            : await _store.SearchNodesAsync(null, name, limit: 10);

        if (candidates.Count == 0)
            return [];

        var results = new List<PublisherInfo>();

        foreach (var node in candidates)
        {
            var path = await _store.TraverseAsync(node.Id, TraceDirection.Inbound, 1,
                [EdgeType.PUBLISHES, EdgeType.CALLS]);
            foreach (var entry in path)
            {
                results.Add(new PublisherInfo(
                    entry.Node.Name,
                    entry.Node.Project,
                    entry.Node.QualifiedName,
                    entry.EdgeType));
            }
        }

        return results.DistinctBy(p => p.QualifiedName).ToList();
    }

    /// <summary>
    /// Get architecture overview for a project.
    /// </summary>
    public async Task<ArchitectureReport> GetArchitectureAsync(string project)
    {
        var nodes = await _store.SearchNodesAsync(project, "%", limit: 10000);
        var summary = await _store.GetProjectSummaryAsync(project);
        var crossRepoEdges = await _store.FindCrossRepoEdgesAsync(project);

        var nodesByLabel = nodes.GroupBy(n => n.Label)
            .ToDictionary(g => g.Key, g => g.Count());

        // Find hotspots: methods with high fan-in
        var methods = nodes.Where(n => n.Label is NodeLabel.Method or NodeLabel.Function).ToList();
        var hotspots = new List<HotspotInfo>();
        foreach (var method in methods.Take(100)) // Cap to avoid excessive queries
        {
            var inbound = await _store.FindEdgesByTargetAsync(method.Id, EdgeType.CALLS);
            if (inbound.Count >= 3)
            {
                hotspots.Add(new HotspotInfo(method.Name, method.QualifiedName,
                    inbound.Count, "High fan-in method"));
            }
        }

        var inboundDeps = crossRepoEdges.Where(e => e.TargetProject == project).ToList();
        var outboundDeps = crossRepoEdges.Where(e => e.SourceProject == project).ToList();

        return new ArchitectureReport(
            project,
            summary?.Summary,
            summary?.Confidence,
            nodesByLabel,
            hotspots.OrderByDescending(h => h.FanIn).Take(10).ToList(),
            inboundDeps,
            outboundDeps);
    }

    /// <summary>
    /// Find repos with no inbound or outbound dependencies.
    /// </summary>
    public async Task<IReadOnlyList<ProjectInfo>> FindArchivalCandidatesAsync()
    {
        var projects = await _store.ListProjectsAsync();
        var candidates = new List<ProjectInfo>();

        foreach (var project in projects)
        {
            var crossRepoEdges = await _store.FindCrossRepoEdgesAsync(project.Name);
            if (crossRepoEdges.Count == 0)
                candidates.Add(project);
        }

        return candidates;
    }

    /// <summary>
    /// Find repos not updated within a given timeframe.
    /// </summary>
    public async Task<IReadOnlyList<ProjectInfo>> FindStaleReposAsync(TimeSpan threshold)
    {
        var projects = await _store.ListProjectsAsync();
        var cutoff = DateTime.UtcNow - threshold;
        return projects.Where(p => p.IndexedAt < cutoff).ToList();
    }
}

// --- Query DTOs ---

public record SearchRequest(
    string NamePattern,
    NodeLabel? Label = null,
    string? Project = null,
    int Limit = 20);

public record SearchResult(
    IReadOnlyList<GraphNode> Nodes,
    int TotalCount);

public record DataLineageResult(
    string ModelName,
    IReadOnlyList<TraversalEntry> Producers,
    IReadOnlyList<TraversalEntry> Consumers,
    IReadOnlyList<CrossRepoEdge> CrossRepoEdges);

public record ConsumerInfo(
    string Name,
    string Project,
    string QualifiedName,
    EdgeType EdgeType);

public record PublisherInfo(
    string Name,
    string Project,
    string QualifiedName,
    EdgeType EdgeType);

public record ArchitectureReport(
    string Project,
    string? Summary,
    ConfidenceLevel? Confidence,
    Dictionary<NodeLabel, int> NodeCounts,
    IReadOnlyList<HotspotInfo> Hotspots,
    IReadOnlyList<CrossRepoEdge> InboundDependencies,
    IReadOnlyList<CrossRepoEdge> OutboundDependencies);

public record HotspotInfo(
    string Name,
    string QualifiedName,
    int FanIn,
    string Reason);
