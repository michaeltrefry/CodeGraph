using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// In-memory buffer for accumulating extraction results before batch flush.
/// Thread-safe for parallel extractor execution.
/// </summary>
public class GraphBuffer
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new(); // keyed by QN
    private readonly ConcurrentDictionary<string, ConcurrentBag<GraphNode>> _nodesByName = new();
    private readonly ConcurrentBag<PendingEdge> _pendingEdges = new();
    private readonly ConcurrentBag<UnresolvedCall> _unresolvedCalls = new();
    private readonly ConcurrentBag<UnresolvedImport> _unresolvedImports = new();
    private readonly ConcurrentDictionary<string, string> _fileHashes = new();

    public void AddNode(GraphNode node)
    {
        _nodes[node.QualifiedName] = node;
        var bag = _nodesByName.GetOrAdd(node.Name, _ => new ConcurrentBag<GraphNode>());
        bag.Add(node);
    }
    public void AddEdge(PendingEdge edge) => _pendingEdges.Add(edge);
    public void AddUnresolvedCall(UnresolvedCall call) => _unresolvedCalls.Add(call);
    public void AddUnresolvedImport(UnresolvedImport import) => _unresolvedImports.Add(import);
    public void AddFileHash(string relPath, string hash) => _fileHashes[relPath] = hash;

    public GraphNode? FindByQN(string qualifiedName)
        => _nodes.TryGetValue(qualifiedName, out var n) ? n : null;

    public IReadOnlyList<GraphNode> FindByName(string name)
        => _nodesByName.TryGetValue(name, out var bag) ? bag.ToList() : [];

    public IReadOnlyList<GraphNode> FindByLabel(NodeLabel label)
        => _nodes.Values.Where(n => n.Label == label).ToList();

    public IReadOnlyCollection<GraphNode> AllNodes => (IReadOnlyCollection<GraphNode>)_nodes.Values;
    public IReadOnlyCollection<PendingEdge> AllPendingEdges => _pendingEdges;
    public IReadOnlyCollection<UnresolvedCall> AllUnresolvedCalls => _unresolvedCalls;
    public IReadOnlyCollection<UnresolvedImport> AllUnresolvedImports => _unresolvedImports;
    public IReadOnlyDictionary<string, string> AllFileHashes => _fileHashes;

    /// <summary>
    /// Resolve pending edges: map QN references to node IDs.
    /// </summary>
    public IReadOnlyList<GraphEdge> ResolveEdges(
        string project, Dictionary<string, long> qnToId, ILogger? logger = null)
    {
        var resolved = new List<GraphEdge>();
        var droppedByType = new Dictionary<EdgeType, int>();
        var droppedSamples = new List<(EdgeType Type, string SourceQN, string TargetQN, string Reason)>();

        foreach (var pending in _pendingEdges)
        {
            var hasSource = qnToId.TryGetValue(pending.SourceQN, out var sourceId);
            var hasTarget = qnToId.TryGetValue(pending.TargetQN, out var targetId);

            if (hasSource && hasTarget)
            {
                resolved.Add(new GraphEdge
                {
                    Project = project,
                    SourceId = sourceId,
                    TargetId = targetId,
                    Type = pending.Type,
                    Properties = pending.Properties ?? new()
                });
            }
            else
            {
                // Track dropped edges for diagnostics
                droppedByType[pending.Type] = droppedByType.GetValueOrDefault(pending.Type) + 1;

                // Collect samples of non-framework dropped edges for debugging
                if (droppedSamples.Count < 20 &&
                    !pending.TargetQN.StartsWith("System.") &&
                    !pending.TargetQN.StartsWith("Microsoft.") &&
                    !pending.TargetQN.StartsWith("object"))
                {
                    var reason = !hasSource ? "source not found" : "target not found";
                    droppedSamples.Add((pending.Type, pending.SourceQN, pending.TargetQN, reason));
                }
            }
        }

        if (logger is not null && droppedByType.Count > 0)
        {
            var total = droppedByType.Values.Sum();
            var breakdown = string.Join(", ", droppedByType
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            logger.LogInformation(
                "Edge resolution: {Resolved} resolved, {Dropped} dropped ({Breakdown})",
                resolved.Count, total, breakdown);

            foreach (var (type, source, target, reason) in droppedSamples)
            {
                logger.LogDebug("  Dropped {Type}: {Source} -> {Target} ({Reason})",
                    type, source, target, reason);
            }
        }

        return resolved;
    }

    public void Clear()
    {
        _nodes.Clear();
        _nodesByName.Clear();
        _pendingEdges.Clear();
        _unresolvedCalls.Clear();
        _unresolvedImports.Clear();
        _fileHashes.Clear();
    }
}
