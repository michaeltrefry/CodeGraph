using System.Collections.Concurrent;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services;

/// <summary>
/// In-memory buffer for accumulating extraction results before batch flush.
/// Thread-safe for parallel extractor execution.
/// </summary>
public class GraphBuffer
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new(); // keyed by QN
    private readonly ConcurrentBag<PendingEdge> _pendingEdges = new();
    private readonly ConcurrentBag<UnresolvedCall> _unresolvedCalls = new();
    private readonly ConcurrentBag<UnresolvedImport> _unresolvedImports = new();
    private readonly ConcurrentDictionary<string, string> _fileHashes = new();

    public void AddNode(GraphNode node) => _nodes[node.QualifiedName] = node;
    public void AddEdge(PendingEdge edge) => _pendingEdges.Add(edge);
    public void AddUnresolvedCall(UnresolvedCall call) => _unresolvedCalls.Add(call);
    public void AddUnresolvedImport(UnresolvedImport import) => _unresolvedImports.Add(import);
    public void AddFileHash(string relPath, string hash) => _fileHashes[relPath] = hash;

    public GraphNode? FindByQN(string qualifiedName)
        => _nodes.TryGetValue(qualifiedName, out var n) ? n : null;

    public IReadOnlyList<GraphNode> FindByName(string name)
        => _nodes.Values.Where(n => n.Name == name).ToList();

    public IReadOnlyList<GraphNode> FindByLabel(NodeLabel label)
        => _nodes.Values.Where(n => n.Label == label).ToList();

    public IReadOnlyCollection<GraphNode> AllNodes => _nodes.Values.ToList();
    public IReadOnlyCollection<PendingEdge> AllPendingEdges => _pendingEdges;
    public IReadOnlyCollection<UnresolvedCall> AllUnresolvedCalls => _unresolvedCalls;
    public IReadOnlyCollection<UnresolvedImport> AllUnresolvedImports => _unresolvedImports;
    public IReadOnlyDictionary<string, string> AllFileHashes => _fileHashes;

    /// <summary>
    /// Resolve pending edges: map QN references to node IDs.
    /// </summary>
    public IReadOnlyList<GraphEdge> ResolveEdges(
        string project, Dictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>();
        foreach (var pending in _pendingEdges)
        {
            if (qnToId.TryGetValue(pending.SourceQN, out var sourceId) &&
                qnToId.TryGetValue(pending.TargetQN, out var targetId))
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
            // Unresolved edges are expected — references to framework types,
            // external packages, etc. Logged at debug level by the pipeline.
        }
        return resolved;
    }

    public void Clear()
    {
        _nodes.Clear();
        _pendingEdges.Clear();
        _unresolvedCalls.Clear();
        _unresolvedImports.Clear();
        _fileHashes.Clear();
    }
}
