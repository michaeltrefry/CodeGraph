using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Services.Query;

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
    public async Task<SearchResult> SearchAsync(SearchRequest request, IGraphReader? graph = null)
    {
        var nodes = await (graph ?? _store).SearchNodesAsync(
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
        int maxDepth = 3, IGraphReader? graph = null)
        => (await TraceCallPathWithStartAsync(functionName, project, direction, maxDepth, graph)).Entries;

    /// <summary>
    /// <see cref="TraceCallPathAsync"/> plus the node the trace started from (null when no node matched).
    /// </summary>
    public async Task<CallPathResult> TraceCallPathWithStartAsync(
        string functionName, string? project, TraceDirection direction,
        int maxDepth = 3, IGraphReader? graph = null)
    {
        var reader = graph ?? _store;
        var candidates = project is not null
            ? await reader.FindNodesByNameAsync(project, functionName)
            : await reader.SearchNodesAsync(null, functionName, limit: 10);

        if (candidates.Count == 0)
            return new CallPathResult(null, []);

        // Prefer Method/Function nodes, fall back to first match
        var startNode = candidates.FirstOrDefault(n =>
            n.Label is NodeLabel.Method or NodeLabel.Function) ?? candidates[0];

        var callEdges = new[] { EdgeType.CALLS, EdgeType.HTTP_CALLS };
        return new CallPathResult(startNode, await reader.TraverseAsync(startNode.Id, direction, maxDepth, callEdges));
    }

    /// <summary>
    /// Trace data lineage: follow a model from origin through all services.
    /// </summary>
    public async Task<DataLineageResult> TraceDataLineageAsync(
        string modelName, string? project, IGraphReader? graph = null)
    {
        var reader = graph ?? _store;
        var candidates = project is not null
            ? await reader.FindNodesByNameAsync(project, modelName)
            : await reader.SearchNodesAsync(null, modelName, limit: 10);

        if (candidates.Count == 0)
            return new DataLineageResult(modelName, [], [], []);

        // Prefer Class/Record/Interface nodes (DTOs/events)
        var modelNode = candidates.FirstOrDefault(n =>
            n.Label is NodeLabel.Class or NodeLabel.Record or NodeLabel.Interface)
            ?? candidates[0];

        // Trace outbound: who consumes/uses this model
        var dataEdges = new[] { EdgeType.USES_TYPE, EdgeType.PUBLISHES, EdgeType.CONSUMES, EdgeType.HTTP_CALLS };
        var outbound = await reader.TraverseAsync(modelNode.Id, TraceDirection.Outbound, 4, dataEdges);

        // Trace inbound: who produces this model
        var inbound = await reader.TraverseAsync(modelNode.Id, TraceDirection.Inbound, 4, dataEdges);

        // Cross-repo edges involving this model's project
        var crossRepo = modelNode.Project is not null
            ? await reader.FindCrossRepoEdgesAsync(modelNode.Project)
            : [];

        return new DataLineageResult(modelName, inbound, outbound, crossRepo) { ModelNode = modelNode };
    }

    /// <summary>
    /// Find all consumers of an event/endpoint/model.
    /// </summary>
    public async Task<IReadOnlyList<ConsumerInfo>> FindConsumersAsync(
        string name, string? project, IGraphReader? graph = null)
    {
        var reader = graph ?? _store;
        var candidates = project is not null
            ? await reader.FindNodesByNameAsync(project, name)
            : await reader.SearchNodesAsync(null, name, limit: 10);

        if (candidates.Count == 0)
            return [];

        var results = new List<ConsumerInfo>();

        // Batch-fetch all inbound edges for all candidates in one query
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var relevantTypes = new[] { EdgeType.CONSUMES, EdgeType.CALLS, EdgeType.HTTP_CALLS,
            EdgeType.USES_TYPE, EdgeType.HANDLES };
        var allEdges = await reader.FindEdgesByTargetBatchAsync(candidateIds, relevantTypes);

        // Group edges by target node, then traverse inbound one hop per distinct edge type
        var edgesByTarget = allEdges.GroupBy(e => e.TargetId);
        foreach (var group in edgesByTarget)
        {
            var distinctTypes = group.Select(e => e.Type).Distinct();
            foreach (var edgeType in distinctTypes)
            {
                var path = await reader.TraverseAsync(group.Key, TraceDirection.Inbound, 1,
                    [edgeType]);
                foreach (var entry in path)
                {
                    results.Add(new ConsumerInfo(
                        entry.Node.Name,
                        entry.Node.Project,
                        entry.Node.QualifiedName,
                        edgeType,
                        GraphNodeConfidence.IsLowConfidence(entry.Node)));
                }
            }
        }

        return results.DistinctBy(c => c.QualifiedName).ToList();
    }

    /// <summary>
    /// Find all publishers to a queue/exchange.
    /// </summary>
    public async Task<IReadOnlyList<PublisherInfo>> FindPublishersAsync(
        string name, string? project, IGraphReader? graph = null)
    {
        var reader = graph ?? _store;
        var candidates = project is not null
            ? await reader.FindNodesByNameAsync(project, name)
            : await reader.SearchNodesAsync(null, name, limit: 10);

        if (candidates.Count == 0)
            return [];

        var results = new List<PublisherInfo>();

        foreach (var node in candidates)
        {
            var path = await reader.TraverseAsync(node.Id, TraceDirection.Inbound, 1,
                [EdgeType.PUBLISHES, EdgeType.CALLS]);
            foreach (var entry in path)
            {
                results.Add(new PublisherInfo(
                    entry.Node.Name,
                    entry.Node.Project,
                    entry.Node.QualifiedName,
                    entry.EdgeType,
                    GraphNodeConfidence.IsLowConfidence(entry.Node)));
            }
        }

        return results.DistinctBy(p => p.QualifiedName).ToList();
    }

    /// <summary>Most hotspots (high fan-in methods) an architecture report lists.</summary>
    public const int ArchitectureHotspotLimit = 10;

    /// <summary>Most supertypes (by implementer/subtype count) an architecture report lists.</summary>
    public const int ArchitectureSupertypeLimit = 10;

    /// <summary>Most external package dependencies an architecture report lists.</summary>
    public const int ArchitecturePackageLimit = 25;

    private const int HotspotMinFanIn = 3;

    private static readonly EdgeType[] CallEdges = [EdgeType.CALLS];
    private static readonly NodeLabel[] CallableLabels = [NodeLabel.Method, NodeLabel.Function];
    private static readonly EdgeType[] SupertypeEdges = [EdgeType.IMPLEMENTS, EdgeType.INHERITS];
    private static readonly EdgeType[] PackageEdges = [EdgeType.REFERENCES_PACKAGE];
    private static readonly NodeLabel[] PackageLabels = [NodeLabel.Package, NodeLabel.NuGetPackage];

    /// <summary>
    /// Get architecture overview for a project. Every figure is aggregated by the store over the
    /// whole graph; only the listed rows are bounded.
    /// </summary>
    public async Task<ArchitectureReport> GetArchitectureAsync(string project)
    {
        var labelCounts = await _store.GetNodeCountsByLabelForProjectAsync(project);
        var summary = await _store.GetRepositorySummaryAsync(project);
        var crossRepoEdges = await _store.FindCrossRepoEdgesAsync(project);

        var nodesByLabel = new Dictionary<NodeLabel, int>();
        foreach (var (label, count) in labelCounts)
        {
            if (Enum.TryParse<NodeLabel>(label, ignoreCase: true, out var parsed))
                nodesByLabel[parsed] = nodesByLabel.GetValueOrDefault(parsed) + count;
        }

        // Hotspots: methods/functions with high CALLS fan-in.
        var hotspots = (await _store.GetTopInDegreeNodesAsync(
                project, CallEdges, CallableLabels, HotspotMinFanIn, ArchitectureHotspotLimit))
            .Select(entry => new HotspotInfo(
                entry.Node.Name,
                entry.Node.QualifiedName,
                entry.InDegree,
                "High fan-in method"))
            .ToList();

        // Key type relationships: the types most implemented/inherited, including external stubs.
        var supertypes = (await _store.GetTopInDegreeNodesAsync(
                project, SupertypeEdges, targetLabels: null, minInDegree: 1, ArchitectureSupertypeLimit))
            .Select(entry => new SupertypeInfo(
                entry.Node.Name,
                entry.Node.QualifiedName,
                entry.Node.Label,
                entry.InDegree,
                IsExternalType(entry.Node)))
            .ToList();

        // External package dependencies (every ecosystem); in-repo package definitions are not
        // dependencies. All referenced packages are counted, only the top rows are listed.
        var packages = (await _store.GetTopInDegreeNodesAsync(
                project, PackageEdges, PackageLabels, minInDegree: 1, limit: int.MaxValue))
            .Where(entry => !IsTrue(entry.Node.Properties.GetValueOrDefault("is_definition")))
            .Select(entry => new PackageDependencyInfo(
                entry.Node.Name,
                PackageEcosystem(entry.Node),
                PackageVersion(entry.Node),
                entry.InDegree))
            .ToList();

        var inboundDeps = crossRepoEdges.Where(e => e.TargetProject == project).ToList();
        var outboundDeps = crossRepoEdges.Where(e => e.SourceProject == project).ToList();

        return new ArchitectureReport(
            project,
            summary?.Summary,
            summary?.Confidence,
            nodesByLabel,
            hotspots,
            inboundDeps,
            outboundDeps,
            packages
                .OrderByDescending(p => p.DependentCount)
                .ThenBy(p => p.Ecosystem, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Version, StringComparer.Ordinal)
                .Take(ArchitecturePackageLimit)
                .ToList(),
            packages.Count,
            packages
                .GroupBy(p => p.Ecosystem, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            supertypes);
    }

    private static string PackageEcosystem(GraphNode node) =>
        node.Label == NodeLabel.NuGetPackage
            ? "nuget"
            : PropertyText(node, "ecosystem") ?? "unknown";

    private static string? PackageVersion(GraphNode node) =>
        PropertyText(node, "version")
        ?? PropertyText(node, "version_requirement")
        ?? PropertyText(node, "revision")
        ?? PropertyText(node, "branch");

    private static bool IsExternalType(GraphNode node) =>
        node.Label == NodeLabel.ExternalSymbol
        || SwiftTypeReference.IsExternal(node.QualifiedName)
        || IsTrue(node.Properties.GetValueOrDefault("scip_external"));

    private static string? PropertyText(GraphNode node, string key)
    {
        var text = node.Properties.GetValueOrDefault(key) switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            var value => value.ToString()
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.String } element =>
            bool.TryParse(element.GetString(), out var parsed) && parsed,
        string text => bool.TryParse(text, out var parsed) && parsed,
        _ => false
    };

    /// <summary>
    /// Find repos with no inbound or outbound dependencies.
    /// </summary>
    public async Task<IReadOnlyList<ProjectInfo>> FindArchivalCandidatesAsync()
    {
        var noEdgeNames = (await _store.FindProjectsWithNoCrossRepoEdgesAsync()).ToHashSet();
        var projects = await _store.ListRepositoriesAsync();
        return projects.Where(p => noEdgeNames.Contains(p.Name)).ToList();
    }

    /// <summary>
    /// Find repos not updated within a given timeframe.
    /// </summary>
    public async Task<IReadOnlyList<ProjectInfo>> FindStaleReposAsync(TimeSpan threshold)
    {
        var projects = await _store.ListRepositoriesAsync();
        var cutoff = DateTime.UtcNow - threshold;
        return projects.Where(p => p.IndexedAt < cutoff).ToList();
    }

    /// <summary>
    /// Get health summary, hotspot files, and AI analysis for a project.
    /// </summary>
    public async Task<ProjectHealthReport> GetProjectHealthAsync(string project, int topHotspots = 10)
    {
        var summaries = await _store.GetProjectHealthSummariesAsync(project);
        var repoSummary = summaries.FirstOrDefault(s => string.IsNullOrEmpty(s.DotnetProject));
        var projectSummaries = summaries.Where(s => !string.IsNullOrEmpty(s.DotnetProject)).ToList();
        var hotspots = await _store.GetHotspotsAsync(project, topHotspots);
        var analyses = await _store.GetProjectHealthAnalysesAsync(project);

        return new ProjectHealthReport(project, repoSummary, projectSummaries, hotspots, analyses);
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

public record CallPathResult(GraphNode? Start, IReadOnlyList<TraversalEntry> Entries);

public record DataLineageResult(
    string ModelName,
    IReadOnlyList<TraversalEntry> Producers,
    IReadOnlyList<TraversalEntry> Consumers,
    IReadOnlyList<CrossRepoEdge> CrossRepoEdges)
{
    /// <summary>The node the lineage was traced from (null when no node matched).</summary>
    public GraphNode? ModelNode { get; init; }
}

public record ConsumerInfo(
    string Name,
    string Project,
    string QualifiedName,
    EdgeType EdgeType,
    bool LowConfidence = false);

public record PublisherInfo(
    string Name,
    string Project,
    string QualifiedName,
    EdgeType EdgeType,
    bool LowConfidence = false);

public record ArchitectureReport(
    string Project,
    string? Summary,
    ConfidenceLevel? Confidence,
    Dictionary<NodeLabel, int> NodeCounts,
    IReadOnlyList<HotspotInfo> Hotspots,
    IReadOnlyList<CrossRepoEdge> InboundDependencies,
    IReadOnlyList<CrossRepoEdge> OutboundDependencies,
    IReadOnlyList<PackageDependencyInfo> PackageDependencies,
    int PackageDependencyCount,
    IReadOnlyDictionary<string, int> PackageDependencyCountsByEcosystem,
    IReadOnlyList<SupertypeInfo> TopSupertypes);

/// <summary>An external package the project depends on, with how many of its manifests/projects reference it.</summary>
public record PackageDependencyInfo(
    string Name,
    string Ecosystem,
    string? Version,
    int DependentCount);

/// <summary>A type the project implements or inherits from, with how many of its types do.</summary>
public record SupertypeInfo(
    string Name,
    string QualifiedName,
    NodeLabel Label,
    int SubtypeCount,
    bool IsExternal);

public record HotspotInfo(
    string Name,
    string QualifiedName,
    int FanIn,
    string Reason);

public record ProjectHealthReport(
    string Project,
    ProjectHealthSummaryEntity? RepoHealth,
    IReadOnlyList<ProjectHealthSummaryEntity> ProjectHealths,
    IReadOnlyList<FileMetricsEntity> TopHotspots,
    IReadOnlyList<ProjectHealthAnalysisEntity> Analyses);
