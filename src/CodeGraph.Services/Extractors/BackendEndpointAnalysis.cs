using System.Collections;
using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>One deterministic allowance shared by endpoint discovery and binding for an index run.</summary>
public sealed class BackendEndpointWorkBudget(long maximumWork = 100_000_000, int maximumDepth = 2048)
{
    public long WorkUsed { get; private set; }
    public int MaximumDepth { get; } = Math.Clamp(maximumDepth, 0, 2048);
    public void Charge(long work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (work < 0 || work > Math.Max(0, maximumWork) - WorkUsed)
            throw new BackendEndpointLimitException("Backend endpoint work budget exhausted");
        WorkUsed += work;
    }
    public void CheckDepth(int depth, CancellationToken ct)
    {
        Charge(1, ct);
        if (depth > MaximumDepth) throw new BackendEndpointLimitException("Backend endpoint AST depth limit exceeded");
    }
}

public sealed class BackendEndpointLimitException(string message) : Exception(message);

/// <summary>Built once per analysis phase, shared across registration files. Candidate queries
/// never enumerate unrelated symbols. Duplicate scoped definitions remain separate candidates.</summary>
public sealed class BackendEndpointHandlerIndex : IReadOnlyCollection<GraphNode>
{
    private readonly List<GraphNode> _nodes = [];
    private readonly Dictionary<(string Extension, string Name, string Scope), List<GraphNode>> _files = [];
    private readonly Dictionary<(string Extension, string Name, string Scope), List<GraphNode>> _directories = [];
    public BackendEndpointHandlerIndex(IEnumerable<GraphNode> nodes, string repository,
        BackendEndpointWorkBudget budget, CancellationToken ct = default)
    {
        foreach (var node in nodes)
        {
            budget.Charge(1, ct);
            if (node.Repository != repository || node.Label is not (NodeLabel.Function or NodeLabel.Method)) continue;
            budget.Charge((node.Repository.Length + node.Name.Length + (node.FilePath?.Length ?? 0)) * 4L, ct);
            var extension = Path.GetExtension(node.FilePath);
            if (extension is not (".rs" or ".go")) continue;
            _nodes.Add(node);
            Add(_files, (extension, node.Name, node.FilePath!), node);
            var directory = node.FilePath!.Contains('/') ? node.FilePath[..node.FilePath.LastIndexOf('/')] : "";
            Add(_directories, (extension, node.Name, directory), node);
        }
    }
    private static void Add(Dictionary<(string, string, string), List<GraphNode>> index,
        (string, string, string) key, GraphNode node)
    {
        if (!index.TryGetValue(key, out var values)) index[key] = values = [];
        values.Add(node);
    }
    public IReadOnlyList<GraphNode> InFile(string extension, string name, string file) =>
        _files.TryGetValue((extension, name, file), out var values) ? values : [];
    public IReadOnlyList<GraphNode> InDirectory(string extension, string name, string directory) =>
        _directories.TryGetValue((extension, name, directory), out var values) ? values : [];
    public int Count => _nodes.Count;
    public IEnumerator<GraphNode> GetEnumerator() => _nodes.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
