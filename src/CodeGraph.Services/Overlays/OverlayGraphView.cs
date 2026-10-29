using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Overlays;

/// <summary>
/// Read-only graph for the head commit of a ready branch overlay: the default-branch graph with
/// the overlay's changed files replacing that graph's nodes and edges for those paths, and the
/// overlay's deleted (and renamed-away) paths removed. Other repositories pass through unchanged.
/// </summary>
/// <remarks>
/// <para>Nodes. Base nodes whose file is changed, deleted or renamed away are hidden. Overlay nodes
/// get negative ids (<c>-overlay node row id</c>) so they never collide with default-branch ids.
/// As in incremental indexing, a node is unique by qualified name within the repository: an overlay
/// node whose qualified name also belongs to a visible base node (one in an unchanged file) is
/// merged into that base node.</para>
/// <para>Edges. Mirroring incremental re-indexing of the changed files: base edges whose source is
/// in a changed or removed file are dropped (the overlay re-extracted those files' outgoing
/// edges); base edges from unchanged files into a changed or removed file are retargeted, by
/// qualified name, to the head node with the same qualified name. A Rust target the head has no
/// qualified-name match for (the default branch may carry SCIP names while overlays always carry
/// tree-sitter names) is retargeted to the head node in the same file with the same name and kind,
/// same enclosing type when known, closest line first; such edges carry
/// <see cref="InferredRetargetProperty"/> and point at low-confidence overlay nodes. Base edges that
/// still cannot be retargeted are dropped and counted in <see cref="UnmappedInboundEdgeCount"/>;
/// answers report only the ones relevant to them (<see cref="CountUnmappedInboundEdges"/>).
/// Overlay edges are resolved by qualified name against the overlay nodes first,
/// then the visible base nodes; an endpoint that resolves to neither is dropped. Cross-repo edges
/// come from the default-branch index and are remapped the same way; cross-repo links that only
/// exist on the branch are not computed (the cross-repo linker runs on the default-branch index).</para>
/// <para>A base node id hidden by the overlay resolves to its head replacement (same qualified
/// name, or the Rust name match above), or to nothing when the head no longer has it.</para>
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
    private readonly HashSet<long> _inferredReplacements = [];
    private readonly Dictionary<(string Path, string Name), List<GraphNode>> _overlayNodesByPathAndName = new();
    private readonly List<(GraphEdge Edge, GraphNode Target)> _unmappedInboundEdges = [];

    /// <summary>
    /// Set on a default-branch edge whose target was mapped to the overlay head by file, name and
    /// kind rather than by qualified name (see <see cref="FindRustNameMatch"/>).
    /// </summary>
    public const string InferredRetargetProperty = "overlayRetarget";

    /// <summary>
    /// Marks an overlay slice entry that removes default-branch data at the overlay head instead
    /// of adding to it: an edge so marked hides the default-branch edge with the same endpoints and
    /// type, and a node so marked hides the file-less default-branch node (a stub) with its
    /// qualified name. Branch overlays emit them when a Swift type reference binds differently at
    /// the head than on the default branch.
    /// </summary>
    public const string RetractedProperty = "overlayRetracted";

    private readonly HashSet<(long Source, long Target, EdgeType Type)> _retractedBaseEdges = [];

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

    public string Repository => Overlay.Repository;

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
        var retractedNodeQns = overlayNodes
            .Where(node => IsRetracted(node.Properties) && string.IsNullOrEmpty(node.FilePath))
            .Select(node => node.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        if (retractedNodeQns.Count > 0)
        {
            foreach (var node in await baseGraph.FindNodesByQualifiedNamesAsync(overlay.Repository, retractedNodeQns))
            {
                if (string.IsNullOrEmpty(node.FilePath))
                    hidden[node.Id] = node;
            }
        }

        foreach (var path in hiddenPaths)
        {
            foreach (var variant in PathVariants(path))
            {
                foreach (var node in await baseGraph.FindNodesByFileAsync(overlay.Repository, variant, int.MaxValue))
                {
                    if (string.Equals(node.Repository, overlay.Repository, StringComparison.OrdinalIgnoreCase)
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
        // Nodes of changed files, plus file-less stubs their edges point at.
        var candidates = overlayNodes
            .Where(node => !IsRetracted(node.Properties)
                           && (_changedPaths.Contains(NormalizePath(node.FilePath))
                               || (string.IsNullOrEmpty(node.FilePath) && IsTrue(node.Properties, "stub"))))
            .GroupBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var qualifiedNames = candidates.Select(node => node.QualifiedName)
            .Concat(overlayEdges.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }))
            .ToHashSet(StringComparer.Ordinal);
        var visibleBaseByQn = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        if (qualifiedNames.Count > 0)
        {
            foreach (var node in await _base.FindNodesByQualifiedNamesAsync(Repository, qualifiedNames))
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

            var effective = node with { Id = overlayId, Repository = Repository };
            _overlayNodes[overlayId] = effective;
            _overlayNodesByQn[node.QualifiedName] = effective;
            effectiveIdByQn[node.QualifiedName] = overlayId;
            var key = (NormalizePath(effective.FilePath), effective.Name);
            if (!_overlayNodesByPathAndName.TryGetValue(key, out var sameName))
                _overlayNodesByPathAndName[key] = sameName = [];
            sameName.Add(effective);
        }

        foreach (var hiddenNode in _hiddenBaseNodes.Values)
        {
            long? replacement = _overlayNodesByQn.TryGetValue(hiddenNode.QualifiedName, out var head) ? head.Id : null;
            if (replacement is null && FindRustNameMatch(hiddenNode) is { } inferred)
            {
                replacement = inferred.Id;
                _inferredReplacements.Add(hiddenNode.Id);
            }

            _hiddenReplacement[hiddenNode.Id] = replacement;
            if (replacement is { } id)
            {
                if (!_replacedBaseIds.TryGetValue(id, out var list))
                    _replacedBaseIds[id] = list = [];
                list.Add(hiddenNode.Id);
            }
        }

        await CollectUnmappedInboundEdgesAsync();

        var seen = new HashSet<(long, long, EdgeType)>();
        foreach (var edge in overlayEdges)
        {
            if (IsRetracted(edge.Properties))
            {
                if (effectiveIdByQn.TryGetValue(edge.SourceQN, out var retractedSource)
                    && effectiveIdByQn.TryGetValue(edge.TargetQN, out var retractedTarget))
                {
                    _retractedBaseEdges.Add((retractedSource, retractedTarget, edge.Type));
                }

                continue;
            }

            if (!effectiveIdByQn.TryGetValue(edge.SourceQN, out var source)
                || !effectiveIdByQn.TryGetValue(edge.TargetQN, out var target)
                || !seen.Add((source, target, edge.Type)))
            {
                continue;
            }

            _overlayEdges.Add(new GraphEdge
            {
                Id = -(_overlayEdges.Count + 1),
                Repository = Repository,
                SourceId = source,
                TargetId = target,
                Type = edge.Type,
                Properties = edge.Properties ?? new()
            });
        }
    }

    /// <summary>
    /// Default-branch edges from unchanged files into a changed or deleted file whose target has no
    /// counterpart at the overlay head (removed, renamed, or not mappable to the branch version),
    /// across the whole overlay. They are left out of every answer at the SHA; an answer reports
    /// only the ones relevant to it (<see cref="CountUnmappedInboundEdges"/>).
    /// </summary>
    public int UnmappedInboundEdgeCount => _unmappedInboundEdges.Count;

    /// <summary>
    /// The unmapped inbound edges (see <see cref="UnmappedInboundEdgeCount"/>) an answer would have
    /// reached on the default branch: edges whose source is one of the answer's nodes, whose target
    /// was in the same file as one of the answer's nodes, or whose target has the queried name.
    /// Nodes of other repositories are ignored. <paramref name="edgeTypes"/>, when given, limits the
    /// count to those edge types.
    /// </summary>
    public int CountUnmappedInboundEdges(
        IEnumerable<(string? Repository, long Id, string? FilePath)> answerNodes,
        string? queriedName = null,
        IReadOnlyCollection<EdgeType>? edgeTypes = null)
    {
        if (_unmappedInboundEdges.Count == 0)
            return 0;

        var ids = new HashSet<long>();
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (repository, id, filePath) in answerNodes)
        {
            if (!IsOverlayRepository(repository))
                continue;
            ids.Add(id);
            if (!string.IsNullOrEmpty(filePath))
                files.Add(NormalizePath(filePath));
        }

        return _unmappedInboundEdges.Count(unmapped =>
            (edgeTypes is null || edgeTypes.Contains(unmapped.Edge.Type))
            && (ids.Contains(unmapped.Edge.SourceId)
                || files.Contains(NormalizePath(unmapped.Target.FilePath))
                || (!string.IsNullOrEmpty(queriedName)
                    && (string.Equals(unmapped.Target.Name, queriedName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(unmapped.Target.QualifiedName, queriedName, StringComparison.OrdinalIgnoreCase)))));
    }

    /// <summary><see cref="CountUnmappedInboundEdges"/> for an answer made of graph nodes.</summary>
    public int CountUnmappedInboundEdges(
        IEnumerable<GraphNode?> answerNodes,
        string? queriedName = null,
        IReadOnlyCollection<EdgeType>? edgeTypes = null)
        => CountUnmappedInboundEdges(
            answerNodes.OfType<GraphNode>().Select(node => ((string?)node.Repository, node.Id, (string?)node.FilePath)),
            queriedName,
            edgeTypes);

    /// <summary><see cref="CountUnmappedInboundEdges"/> for an impact report's changed and affected nodes.</summary>
    public int CountUnmappedInboundEdges(ImpactReport report, string? queriedName = null)
        => CountUnmappedInboundEdges(
            report.ChangedNodes.Concat(report.AffectedNodes)
                .Select(node => ((string?)node.Repository, node.NodeId, (string?)node.FilePath)),
            queriedName);

    /// <summary>
    /// Base Rust nodes whose qualified name the overlay does not carry (a semantic, SCIP-named
    /// default-branch index against tree-sitter overlay names) are mapped to the overlay node in the
    /// same file with the same name and kind family. When both sides know the enclosing type (the
    /// impl's type), it must match; among the remaining candidates the closest start line wins, and
    /// a tie or no candidate leaves the node unmapped. Edges retargeted this way are inferred.
    /// </summary>
    private GraphNode? FindRustNameMatch(GraphNode hidden)
    {
        var path = NormalizePath(hidden.FilePath);
        if (!path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase) || !_changedPaths.Contains(path))
            return null;
        if (!_overlayNodesByPathAndName.TryGetValue((path, hidden.Name), out var sameName))
            return null;

        var family = KindFamily(hidden.Label);
        var enclosing = ScipEnclosingType(hidden.QualifiedName);
        var candidates = sameName
            .Where(node => KindFamily(node.Label) == family)
            .Where(node => enclosing is null
                           || TreeSitterEnclosingType(node) is not { } nodeEnclosing
                           || string.Equals(nodeEnclosing, enclosing, StringComparison.Ordinal))
            .Select(node => (Node: node, Distance: Math.Abs(node.StartLine - hidden.StartLine)))
            .OrderBy(candidate => candidate.Distance)
            .Take(2)
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (candidates.Count > 1 && candidates[1].Distance == candidates[0].Distance)
            return null;
        return candidates[0].Node;
    }

    // SCIP and tree-sitter label the same Rust item differently (a struct is Struct vs Class, an
    // impl fn is Method vs Function); match within a family.
    private static string KindFamily(NodeLabel label) => label switch
    {
        NodeLabel.Function or NodeLabel.Method or NodeLabel.Constructor => "callable",
        NodeLabel.Class or NodeLabel.Struct or NodeLabel.Enum or NodeLabel.Interface or NodeLabel.Record => "type",
        _ => label.ToString()
    };

    // A SCIP callable descriptor ends `name().` (or `name(+1).`), preceded by its owner: `Type#` for
    // an inherent method, `impl#[Type][Trait]` for a trait impl method, `/` (or nothing) for a free
    // function in a module.
    private static readonly Regex ScipCallable = new(
        @"(?:impl#\[(?<impl>[^\[\]]+)\](?:\[[^\[\]]*\])?|(?<type>`[^`]+`|[^\s/#.()\[\]`]+)#|(?<free>/|^))(?:`[^`]+`|[^\s/#.()\[\]`]+)\([^()]*\)\.$",
        RegexOptions.CultureInvariant);

    // A tree-sitter callable qualified name: `...#type:Type#method:name(...)` or `...#function:name(...)`.
    private static readonly Regex TreeSitterCallable = new(
        @"(?:#type:(?<type>[^#]+))?#(?<kind>method|function):[^#]*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The enclosing type of a SCIP-named Rust callable: the type name, "" for a free function, or
    /// null when the name is not a SCIP callable descriptor this recognizes.
    /// </summary>
    internal static string? ScipEnclosingType(string qualifiedName)
    {
        const string marker = ":scip:";
        var at = qualifiedName.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var symbol = qualifiedName[(at + marker.Length)..];
        var match = ScipCallable.Match(symbol[(symbol.LastIndexOf(' ') + 1)..]);
        if (!match.Success)
            return null;
        if (match.Groups["impl"].Success)
            return match.Groups["impl"].Value.Trim('`');
        return match.Groups["type"].Success ? match.Groups["type"].Value.Trim('`') : "";
    }

    /// <summary>
    /// The enclosing type of a tree-sitter Rust callable (its <c>receiver_owner</c>, else parsed from
    /// the qualified name): the type name, "" for a free function, or null when unknown.
    /// </summary>
    internal static string? TreeSitterEnclosingType(GraphNode node)
    {
        var owner = node.Properties.GetValueOrDefault("receiver_owner") switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        if (!string.IsNullOrEmpty(owner))
            return owner;

        var match = TreeSitterCallable.Match(node.QualifiedName);
        if (!match.Success)
            return null;
        if (match.Groups["type"].Success)
            return match.Groups["type"].Value;
        return match.Groups["kind"].Value == "function" ? "" : null;
    }

    private async Task CollectUnmappedInboundEdgesAsync()
    {
        var unmapped = _hiddenReplacement.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList();
        if (unmapped.Count == 0)
            return;

        foreach (var edge in await _base.FindEdgesByTargetBatchAsync(unmapped))
        {
            if (!_hiddenBaseNodes.ContainsKey(edge.SourceId)
                && !StructuralEdgeTypes.Contains(edge.Type)
                && _hiddenBaseNodes.TryGetValue(edge.TargetId, out var target))
            {
                _unmappedInboundEdges.Add((edge, target));
            }
        }
    }

    private static readonly HashSet<EdgeType> StructuralEdgeTypes =
    [
        EdgeType.CONTAINS_FILE, EdgeType.CONTAINS_FOLDER, EdgeType.CONTAINS_NAMESPACE,
        EdgeType.CONTAINS_PROJECT, EdgeType.DEFINES, EdgeType.DEFINES_METHOD
    ];

    /// <summary>True when <paramref name="relativePath"/> was deleted (or renamed away) at the overlay head.</summary>
    public bool IsRemovedPath(string relativePath) => _removedPaths.Contains(NormalizePath(relativePath));

    /// <summary>True when <paramref name="relativePath"/> was added, modified or renamed to at the overlay head.</summary>
    public bool IsChangedPath(string relativePath) => _changedPaths.Contains(NormalizePath(relativePath));

    public bool IsOverlayRepository(string? repository)
        => repository is not null && string.Equals(repository, Repository, StringComparison.OrdinalIgnoreCase);

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

    public async Task<GraphNode?> FindNodeByQualifiedNameAsync(string repository, string qualifiedName)
    {
        if (!IsOverlayRepository(repository))
            return await _base.FindNodeByQualifiedNameAsync(repository, qualifiedName);
        if (_overlayNodesByQn.TryGetValue(qualifiedName, out var node))
            return node;

        var baseNode = await _base.FindNodeByQualifiedNameAsync(repository, qualifiedName);
        return baseNode is null || IsHidden(baseNode) ? null : baseNode;
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByQualifiedNamesAsync(
        string repository,
        IReadOnlyCollection<string> qualifiedNames)
    {
        var baseNodes = await _base.FindNodesByQualifiedNamesAsync(repository, qualifiedNames);
        if (!IsOverlayRepository(repository))
            return baseNodes;

        var names = qualifiedNames.ToHashSet(StringComparer.Ordinal);
        return baseNodes.Where(node => !IsHidden(node))
            .Concat(_overlayNodes.Values.Where(node => names.Contains(node.QualifiedName)))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string repository, string name, int limit = 1000)
    {
        if (!IsOverlayRepository(repository))
            return await _base.FindNodesByNameAsync(repository, name, limit);

        var baseNodes = await _base.FindNodesByNameAsync(repository, name, SaturatingAdd(limit, _hiddenBaseNodes.Count));
        return baseNodes.Where(node => !IsHidden(node))
            .Concat(_overlayNodes.Values
                .Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => -node.Id))
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string repository, string filePath, int limit = 5000)
    {
        if (!IsOverlayRepository(repository))
            return await _base.FindNodesByFileAsync(repository, filePath, limit);

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

        return (await _base.FindNodesByFileAsync(repository, filePath, limit))
            .Where(node => !IsHidden(node))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? repository,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        int limit = 50,
        int offset = 0,
        string? dotnetProject = null)
    {
        if (repository is not null && !IsOverlayRepository(repository))
            return await _base.SearchNodesAsync(repository, namePattern, label, filePattern, limit, offset, dotnetProject);

        var window = SaturatingAdd(SaturatingAdd(offset, limit), _hiddenBaseNodes.Count);
        var baseNodes = (await _base.SearchNodesAsync(repository, namePattern, label, filePattern, window, 0, dotnetProject))
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
        string? repository,
        string namePattern,
        NodeLabel? label = null,
        string? filePattern = null,
        string? dotnetProject = null)
    {
        var baseCount = await _base.SearchNodesCountAsync(repository, namePattern, label, filePattern, dotnetProject);
        if (repository is not null && !IsOverlayRepository(repository))
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

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string repository, EdgeType? type = null)
    {
        var edges = await _base.FindCrossRepoEdgesAsync(repository, type);
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
        => _hiddenBaseNodes.ContainsKey(node.Id) && IsOverlayRepository(node.Repository);

    /// <summary>Applies the edge rules from the class remarks to a default-branch edge.</summary>
    private GraphEdge? MapBaseEdge(GraphEdge edge)
    {
        if (_hiddenBaseNodes.ContainsKey(edge.SourceId))
            return null;
        if (_retractedBaseEdges.Count > 0)
        {
            var target = Canonical(edge.TargetId);
            if (target is { } mapped && _retractedBaseEdges.Contains((edge.SourceId, mapped, edge.Type)))
                return null;
        }

        return MapVisibleBaseEdge(edge);
    }

    private GraphEdge? MapVisibleBaseEdge(GraphEdge edge)
    {
        if (!_hiddenReplacement.TryGetValue(edge.TargetId, out var replacement))
            return edge;
        if (replacement is not { } target)
            return null;
        if (!_inferredReplacements.Contains(edge.TargetId))
            return edge with { TargetId = target };

        var properties = new Dictionary<string, object>(edge.Properties ?? new())
        {
            [InferredRetargetProperty] = "name-match"
        };
        return edge with { TargetId = target, Properties = properties };
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

    private static bool IsRetracted(IReadOnlyDictionary<string, object>? properties)
        => IsTrue(properties, RetractedProperty);

    private static bool IsTrue(IReadOnlyDictionary<string, object>? properties, string key)
        => properties?.GetValueOrDefault(key) switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };

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
