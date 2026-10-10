using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Services.Overlays;

/// <summary>
/// Read-only graph for the head commit of a ready branch overlay: the default-branch graph with
/// the overlay's changed files replacing that graph's nodes and edges for those paths, and the
/// overlay's deleted (and renamed-away) paths removed. Other projects pass through unchanged.
/// </summary>
/// <remarks>
/// <para>Nodes. Base nodes whose file is changed, deleted or renamed away are hidden. Overlay nodes
/// get negative ids (<c>-overlay node row id</c>) so they never collide with default-branch ids.
/// As in incremental indexing, a node is unique by qualified name within the project: an overlay
/// node whose qualified name also belongs to a visible base node (one in an unchanged file) is
/// merged into that base node.</para>
/// <para>Edges. Mirroring incremental re-indexing of the changed files: base edges whose source is
/// in a changed or removed file are dropped (the overlay re-extracted those files' outgoing
/// edges); base edges from unchanged files into a changed or removed file are retargeted, by
/// qualified name, to the head node with the same qualified name, and dropped when the head no
/// longer has one. Overlay edges are resolved by qualified name against the overlay nodes first,
/// then the visible base nodes; an endpoint that resolves to neither is dropped. Cross-repo edges
/// come from the default-branch index and are remapped the same way; cross-repo links that only
/// exist on the branch are not computed (the cross-repo linker runs on the default-branch index).</para>
/// <para>A base node id hidden by the overlay resolves to its head replacement (same qualified
/// name), or to nothing when the head no longer has it.</para>
/// </remarks>
public sealed class OverlayGraphView : IGraphReader
{
    private readonly IGraphReader _base;
    private readonly HashSet<string> _changedPaths;
    private readonly HashSet<string> _removedPaths;
    private readonly Dictionary<long, GraphNode> _overlayNodes = new();
    private readonly Dictionary<string, GraphNode> _overlayNodesByQn = new(StringComparer.Ordinal);
    private readonly Dictionary<long, long> _mergedOverlayIds = new();
    private readonly Dictionary<long, GraphNode> _hiddenBaseNodes;
    private readonly Dictionary<long, long?> _hiddenReplacement = new();
    private readonly Dictionary<long, List<long>> _replacedBaseIds = new();
    private readonly List<GraphEdge> _overlayEdges = [];

    private OverlayGraphView(
        IGraphReader baseGraph,
        BranchOverlayEntity overlay,
        HashSet<string> changedPaths,
        HashSet<string> removedPaths,
        Dictionary<long, GraphNode> hiddenBaseNodes)
    {
        _base = baseGraph;
        Overlay = overlay;
        _changedPaths = changedPaths;
        _removedPaths = removedPaths;
        _hiddenBaseNodes = hiddenBaseNodes;
    }

    public BranchOverlayEntity Overlay { get; }

    public string Project => Overlay.Project;

    public static async Task<OverlayGraphView> CreateAsync(
        IGraphReader baseGraph,
        BranchOverlayEntity overlay,
        IReadOnlyList<BranchOverlayFileEntity> files,
        IReadOnlyList<GraphNode> overlayNodes,
        IReadOnlyList<PendingEdge> overlayEdges)
    {
        var changed = files
            .Where(file => file.ChangeKind != BranchOverlayChangeKinds.Deleted)
            .Select(file => NormalizePath(file.FilePath))
            .ToHashSet(StringComparer.Ordinal);
        var removed = files
            .Where(file => file.ChangeKind == BranchOverlayChangeKinds.Deleted)
            .Select(file => NormalizePath(file.FilePath))
            .Where(path => !changed.Contains(path))
            .ToHashSet(StringComparer.Ordinal);

        var hiddenPaths = changed.Concat(removed).ToHashSet(StringComparer.Ordinal);
        var hidden = new Dictionary<long, GraphNode>();
        foreach (var path in hiddenPaths)
        {
            foreach (var variant in PathVariants(path))
            {
                foreach (var node in await baseGraph.FindNodesByFileAsync(overlay.Project, variant, int.MaxValue))
                {
                    if (string.Equals(node.Project, overlay.Project, StringComparison.OrdinalIgnoreCase)
                        && hiddenPaths.Contains(NormalizePath(node.FilePath)))
                    {
                        hidden[node.Id] = node;
                    }
                }
            }
        }

        var view = new OverlayGraphView(baseGraph, overlay, changed, removed, hidden);
        await view.BuildAsync(overlayNodes, overlayEdges);
        return view;
    }

    private async Task BuildAsync(IReadOnlyList<GraphNode> overlayNodes, IReadOnlyList<PendingEdge> overlayEdges)
    {
        var candidates = overlayNodes
            .Where(node => _changedPaths.Contains(NormalizePath(node.FilePath)))
            .GroupBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var qualifiedNames = candidates.Select(node => node.QualifiedName)
            .Concat(overlayEdges.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }))
            .ToHashSet(StringComparer.Ordinal);
        var visibleBaseByQn = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        if (qualifiedNames.Count > 0)
        {
            foreach (var node in await _base.FindNodesByQualifiedNamesAsync(Project, qualifiedNames))
            {
                if (!_hiddenBaseNodes.ContainsKey(node.Id))
                    visibleBaseByQn.TryAdd(node.QualifiedName, node);
            }
        }

        var effectiveIdByQn = visibleBaseByQn.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            var node = candidates[i];
            var overlayId = -(node.Id > 0 ? node.Id : i + 1);
            if (visibleBaseByQn.TryGetValue(node.QualifiedName, out var baseNode))
            {
                _mergedOverlayIds[overlayId] = baseNode.Id;
                continue;
            }

            var effective = node with { Id = overlayId, Project = Project };
            _overlayNodes[overlayId] = effective;
            _overlayNodesByQn[node.QualifiedName] = effective;
            effectiveIdByQn[node.QualifiedName] = overlayId;
        }

        foreach (var hiddenNode in _hiddenBaseNodes.Values)
        {
            long? replacement = _overlayNodesByQn.TryGetValue(hiddenNode.QualifiedName, out var head) ? head.Id : null;
            _hiddenReplacement[hiddenNode.Id] = replacement;
            if (replacement is { } id)
            {
                if (!_replacedBaseIds.TryGetValue(id, out var list))
                    _replacedBaseIds[id] = list = [];
                list.Add(hiddenNode.Id);
            }
        }

        var seen = new HashSet<(long, long, EdgeType)>();
        foreach (var edge in overlayEdges)
        {
            if (!effectiveIdByQn.TryGetValue(edge.SourceQN, out var source)
                || !effectiveIdByQn.TryGetValue(edge.TargetQN, out var target)
                || !seen.Add((source, target, edge.Type)))
            {
                continue;
            }

            _overlayEdges.Add(new GraphEdge
            {
                Id = -(_overlayEdges.Count + 1),
                Project = Project,
                SourceId = source,
                TargetId = target,
                Type = edge.Type,
                Properties = edge.Properties ?? new()
            });
        }
    }

    /// <summary>True when <paramref name="relativePath"/> was deleted (or renamed away) at the overlay head.</summary>
    public bool IsRemovedPath(string relativePath) => _removedPaths.Contains(NormalizePath(relativePath));

    /// <summary>True when <paramref name="relativePath"/> was added, modified or renamed to at the overlay head.</summary>
    public bool IsChangedPath(string relativePath) => _changedPaths.Contains(NormalizePath(relativePath));

    public bool IsOverlayProject(string? project)
        => project is not null && string.Equals(project, Project, StringComparison.OrdinalIgnoreCase);

    // ── Nodes ────────────────────────────────────────────────────────────

    public async Task<GraphNode?> FindNodeByIdAsync(long id)
        => (await FindNodesByIdBatchAsync([id])).GetValueOrDefault(id);

    public async Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, GraphNode>();
        var baseRequests = new Dictionary<long, long>();
        foreach (var id in ids.Distinct())
        {
            if (Canonical(id) is not { } canonical)
                continue;
            if (canonical < 0)
            {
                if (_overlayNodes.TryGetValue(canonical, out var node))
                    result[id] = node;
            }
            else
            {
                baseRequests[id] = canonical;
            }
        }

        if (baseRequests.Count > 0)
        {
            var nodes = await _base.FindNodesByIdBatchAsync(baseRequests.Values.Distinct().ToList());
            foreach (var (requested, baseId) in baseRequests)
            {
                if (nodes.TryGetValue(baseId, out var node))
                    result[requested] = node;
            }
        }

        return result;
    }

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
    {
        if (!IsOverlayProject(project))
            return await _base.FindNodeByQualifiedNameAsync(project, qualifiedName);
        if (_overlayNodesByQn.TryGetValue(qualifiedName, out var node))
            return node;

        var baseNode = await _base.FindNodeByQualifiedNameAsync(project, qualifiedName);
        return baseNode is null || IsHidden(baseNode) ? null : baseNode;
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByQualifiedNamesAsync(
        string project,
        IReadOnlyCollection<string> qualifiedNames)
    {
        var baseNodes = await _base.FindNodesByQualifiedNamesAsync(project, qualifiedNames);
        if (!IsOverlayProject(project))
            return baseNodes;

        var names = qualifiedNames.ToHashSet(StringComparer.Ordinal);
        return baseNodes.Where(node => !IsHidden(node))
            .Concat(_overlayNodes.Values.Where(node => names.Contains(node.QualifiedName)))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000)
    {
        if (!IsOverlayProject(project))
            return await _base.FindNodesByNameAsync(project, name, limit);

        var baseNodes = await _base.FindNodesByNameAsync(project, name, SaturatingAdd(limit, _hiddenBaseNodes.Count));
        return baseNodes.Where(node => !IsHidden(node))
            .Concat(_overlayNodes.Values
                .Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => -node.Id))
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000)
    {
        if (!IsOverlayProject(project))
            return await _base.FindNodesByFileAsync(project, filePath, limit);

        var path = NormalizePath(filePath);
        if (_removedPaths.Contains(path))
            return [];
        if (_changedPaths.Contains(path))
        {
            return _overlayNodes.Values
                .Where(node => NormalizePath(node.FilePath) == path)
                .OrderBy(node => -node.Id)
                .Take(limit)
                .ToList();
        }

        return (await _base.FindNodesByFileAsync(project, filePath, limit))
            .Where(node => !IsHidden(node))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? project,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        int limit = 50,
        int offset = 0,
        string? dotnetProject = null)
    {
        if (project is not null && !IsOverlayProject(project))
            return await _base.SearchNodesAsync(project, namePattern, label, filePattern, limit, offset, dotnetProject);

        var window = SaturatingAdd(SaturatingAdd(offset, limit), _hiddenBaseNodes.Count);
        var baseNodes = (await _base.SearchNodesAsync(project, namePattern, label, filePattern, window, 0, dotnetProject))
            .Where(node => !IsHidden(node))
            .ToList();
        var matcher = new NodeFilter(namePattern, label, filePattern, dotnetProject);
        var overlayNodes = _overlayNodes.Values
            .Where(matcher.Matches)
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => -node.Id)
            .ToList();

        return MergeByName(baseNodes, overlayNodes).Skip(offset).Take(limit).ToList();
    }

    public async Task<int> SearchNodesCountAsync(
        string? project,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        string? dotnetProject = null)
    {
        var baseCount = await _base.SearchNodesCountAsync(project, namePattern, label, filePattern, dotnetProject);
        if (project is not null && !IsOverlayProject(project))
            return baseCount;

        var matcher = new NodeFilter(namePattern, label, filePattern, dotnetProject);
        return baseCount
               - _hiddenBaseNodes.Values.Count(matcher.Matches)
               + _overlayNodes.Values.Count(matcher.Matches);
    }

    // ── Edges ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
        => FindEdgesBySourceBatchAsync([sourceId], type is null ? null : [type.Value]);

    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
        => FindEdgesByTargetBatchAsync([targetId], type is null ? null : [type.Value]);

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceBatchAsync(
        IReadOnlyList<long> sourceIds,
        EdgeType[]? types = null)
    {
        var canonical = CanonicalSet(sourceIds);
        var baseSources = canonical.Where(id => id > 0).ToList();
        var baseEdges = baseSources.Count > 0
            ? await _base.FindEdgesBySourceBatchAsync(baseSources, types)
            : [];
        var overlayEdges = _overlayEdges.Where(edge => canonical.Contains(edge.SourceId) && TypeMatches(edge, types));
        return Combine(baseEdges, overlayEdges);
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(
        IReadOnlyList<long> targetIds,
        EdgeType[]? types = null)
    {
        var canonical = CanonicalSet(targetIds);
        var baseTargets = canonical.Where(id => id > 0)
            .Concat(canonical.Where(id => id < 0).SelectMany(id => _replacedBaseIds.GetValueOrDefault(id) ?? []))
            .Distinct()
            .ToList();
        var baseEdges = baseTargets.Count > 0
            ? await _base.FindEdgesByTargetBatchAsync(baseTargets, types)
            : [];
        var overlayEdges = _overlayEdges.Where(edge => canonical.Contains(edge.TargetId) && TypeMatches(edge, types));
        return Combine(baseEdges, overlayEdges);
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string project, EdgeType? type = null)
    {
        var edges = await _base.FindCrossRepoEdgesAsync(project, type);
        var result = new List<CrossRepoEdge>(edges.Count);
        foreach (var edge in edges)
        {
            var source = Canonical(edge.SourceNodeId);
            var target = Canonical(edge.TargetNodeId);
            if (source is null || target is null)
                continue;
            result.Add(edge with { SourceNodeId = source.Value, TargetNodeId = target.Value });
        }

        return result;
    }

    public async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(
        long startNodeId,
        TraceDirection direction,
        int maxDepth,
        EdgeType[]? edgeFilter = null,
        double minConfidence = 0)
    {
        if (Canonical(startNodeId) is not { } start)
            return [];

        var types = edgeFilter is { Length: > 0 } ? edgeFilter : null;
        var visited = new HashSet<long> { start };
        var frontier = new List<long> { start };
        var found = new List<(long Node, int Depth, EdgeType Type, long Parent, Dictionary<string, object> Properties)>();

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var steps = new List<(long Parent, long Child, GraphEdge Edge)>();
            if (direction is TraceDirection.Outbound or TraceDirection.Both)
            {
                steps.AddRange((await FindEdgesBySourceBatchAsync(frontier, types))
                    .Select(edge => (edge.SourceId, edge.TargetId, edge)));
            }

            if (direction is TraceDirection.Inbound or TraceDirection.Both)
            {
                steps.AddRange((await FindEdgesByTargetBatchAsync(frontier, types))
                    .Select(edge => (edge.TargetId, edge.SourceId, edge)));
            }

            var next = new List<long>();
            foreach (var (parent, child, edge) in steps)
            {
                if (minConfidence > 0 && ReadConfidence(edge.Properties) is { } confidence && confidence < minConfidence)
                    continue;
                if (!visited.Add(child))
                    continue;
                next.Add(child);
                found.Add((child, depth, edge.Type, parent, edge.Properties));
            }

            frontier = next;
        }

        var nodes = await FindNodesByIdBatchAsync(found.Select(entry => entry.Node).ToList());
        return found
            .Where(entry => nodes.ContainsKey(entry.Node))
            .Select(entry => new TraversalEntry(
                nodes[entry.Node], entry.Depth, entry.Type, entry.Parent, entry.Properties))
            .OrderBy(entry => entry.Depth)
            .ThenBy(entry => entry.Node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// The id a node has in this view: overlay ids stay, a merged overlay id becomes its base id,
    /// a hidden base id becomes its head replacement (or null), and visible base ids stay.
    /// </summary>
    private long? Canonical(long id)
    {
        if (id < 0)
        {
            if (_overlayNodes.ContainsKey(id))
                return id;
            return _mergedOverlayIds.TryGetValue(id, out var merged) ? merged : null;
        }

        return _hiddenReplacement.TryGetValue(id, out var replacement) ? replacement : id;
    }

    private HashSet<long> CanonicalSet(IEnumerable<long> ids)
        => ids.Select(Canonical).OfType<long>().ToHashSet();

    private bool IsHidden(GraphNode node)
        => _hiddenBaseNodes.ContainsKey(node.Id) && IsOverlayProject(node.Project);

    /// <summary>Applies the edge rules from the class remarks to a default-branch edge.</summary>
    private GraphEdge? MapBaseEdge(GraphEdge edge)
    {
        if (_hiddenBaseNodes.ContainsKey(edge.SourceId))
            return null;
        if (!_hiddenReplacement.TryGetValue(edge.TargetId, out var replacement))
            return edge;
        return replacement is { } target ? edge with { TargetId = target } : null;
    }

    private IReadOnlyList<GraphEdge> Combine(IEnumerable<GraphEdge> baseEdges, IEnumerable<GraphEdge> overlayEdges)
    {
        var seen = new HashSet<(long, long, EdgeType)>();
        var result = new List<GraphEdge>();
        foreach (var edge in baseEdges.Select(MapBaseEdge).OfType<GraphEdge>().Concat(overlayEdges))
        {
            if (seen.Add((edge.SourceId, edge.TargetId, edge.Type)))
                result.Add(edge);
        }

        return result;
    }

    private static bool TypeMatches(GraphEdge edge, EdgeType[]? types)
        => types is not { Length: > 0 } || types.Contains(edge.Type);

    private static IEnumerable<GraphNode> MergeByName(IReadOnlyList<GraphNode> baseNodes, IReadOnlyList<GraphNode> overlayNodes)
    {
        // Keeps the store's order for base nodes and inserts overlay nodes by name.
        int b = 0, o = 0;
        while (b < baseNodes.Count || o < overlayNodes.Count)
        {
            if (o >= overlayNodes.Count
                || (b < baseNodes.Count
                    && string.Compare(baseNodes[b].Name, overlayNodes[o].Name, StringComparison.OrdinalIgnoreCase) <= 0))
            {
                yield return baseNodes[b++];
            }
            else
            {
                yield return overlayNodes[o++];
            }
        }
    }

    internal static double? ReadConfidence(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties is null || !properties.TryGetValue("confidence", out var value) || value is null)
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element
                when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    internal static string NormalizePath(string? path)
        => (path ?? "").Replace('\\', '/');

    private static IEnumerable<string> PathVariants(string path)
    {
        yield return path;
        var backslashed = path.Replace('/', '\\');
        if (backslashed != path)
            yield return backslashed;
    }

    private static int SaturatingAdd(int a, int b)
        => (int)Math.Min(int.MaxValue, (long)a + b);

    /// <summary>In-memory equivalent of the store's node search filters (name/file are SQL LIKE '%pattern%').</summary>
    private sealed class NodeFilter(string namePattern, NodeLabel? label, string? filePattern, string? dotnetProject)
    {
        private readonly Regex? _name = string.IsNullOrEmpty(namePattern) ? null : ContainsLike(namePattern);
        private readonly Regex? _file = string.IsNullOrEmpty(filePattern) ? null : ContainsLike(filePattern);

        public bool Matches(GraphNode node)
            => (_name is null || _name.IsMatch(node.Name))
               && (label is null || node.Label == label)
               && (_file is null || _file.IsMatch(node.FilePath ?? ""))
               && (dotnetProject is null || node.DotnetProject == dotnetProject);

        private static Regex ContainsLike(string pattern)
        {
            var body = string.Concat(pattern.Select(c => c switch
            {
                '%' => ".*",
                '_' => ".",
                _ => Regex.Escape(c.ToString())
            }));
            return new Regex($"^.*{body}.*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
    }
}
