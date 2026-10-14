using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Cross-file resolution of Swift type references (see <see cref="SwiftTypeReference"/>).
/// </summary>
public partial class IndexingPipeline
{
    private const int MaxLoggedAmbiguousSwiftTypes = 20;

    private static bool IsSwiftTypeNode(GraphNode node) =>
        node.Label is NodeLabel.Class or NodeLabel.Interface or NodeLabel.Struct or NodeLabel.Enum
        && node.FilePath?.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Persisted Swift edges an incremental run or overlay re-binds: <see cref="ReboundEdges"/>
    /// replace <see cref="Retractions"/>' edges, and stubs left without edges are removed.
    /// </summary>
    internal sealed record SwiftRebinding(
        IReadOnlyList<PendingEdge> ReboundEdges,
        GraphSliceRetractions Retractions)
    {
        public static readonly SwiftRebinding None = new([], new GraphSliceRetractions([], []));
    }

    /// <summary>
    /// Resolves Swift type references. An unqualified path binds to the single top-level Swift
    /// type of that name in the repository (this run's files, else the persisted graph outside the
    /// re-indexed files); a qualified path (<c>Outer.Inner</c>) to the single type at that path. A
    /// class's inferred first supertype takes the bound type's kind: IMPLEMENTS for a protocol,
    /// INHERITS otherwise. A path matching no repository type is external and binds to a stub
    /// node. An ambiguous extended type leaves its members under the extension's file; an
    /// ambiguous supertype yields no edge.
    /// </summary>
    /// <remarks>
    /// With <paramref name="includePersistedNodes"/>, previously resolved edges stay correct
    /// without re-extracting their files: every persisted edge whose reference names a Swift type
    /// declared in this run's files, or in a re-indexed or deleted file, is resolved again. An
    /// edge whose binding changed is retracted and its new binding returned; an unchanged edge
    /// whose source node is re-created by this run is re-emitted; persisted edges emitted by a
    /// re-indexed file are retracted (its extraction emits their current form). Stubs of the
    /// affected names are orphan candidates, deleted by the write once nothing references them.
    /// </remarks>
    internal async Task<SwiftRebinding> ResolveSwiftTypeReferencesAsync(
        string projectName,
        GraphBuffer buffer,
        bool includePersistedNodes,
        IReadOnlySet<string> excludedPersistedFiles,
        CancellationToken ct)
    {
        var symbolic = buffer.AllPendingEdges
            .Where(edge => SwiftTypeReference.TryParse(edge.SourceQN, out _)
                           || SwiftTypeReference.TryParse(edge.TargetQN, out _))
            .ToList();

        var affectedNames = new HashSet<string>(StringComparer.Ordinal);
        List<StoredEdge> persistedReferences = [];
        List<StoredEdge> originOwned = [];
        if (includePersistedNodes)
        {
            var excludedSwiftFiles = excludedPersistedFiles
                .Where(file => file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
                .ToList();
            affectedNames.UnionWith(buffer.AllNodes.Where(IsSwiftTypeNode).Select(node => node.Name));
            if (excludedSwiftFiles.Count > 0)
            {
                affectedNames.UnionWith((await _store.FindNodesByFilesAsync(projectName, excludedSwiftFiles, ct))
                    .Where(IsSwiftTypeNode)
                    .Select(node => node.Name));
                originOwned = (await _store.FindEdgesByPropertyAsync(
                    projectName, SwiftTypeReference.OriginFileProperty, excludedSwiftFiles, ct)).ToList();
            }

            if (affectedNames.Count > 0)
            {
                var bySource = await _store.FindEdgesByPropertyAsync(
                    projectName, SwiftTypeReference.SourceNameProperty, affectedNames, ct);
                var byTarget = await _store.FindEdgesByPropertyAsync(
                    projectName, SwiftTypeReference.TargetNameProperty, affectedNames, ct);
                persistedReferences = bySource.Concat(byTarget)
                    .DistinctBy(StoredEdgeKey)
                    .Where(edge => !excludedPersistedFiles.Contains(NormalizeRelativePath(
                        PlainString(edge.Properties, SwiftTypeReference.OriginFileProperty) ?? "")))
                    .ToList();
            }
        }

        if (symbolic.Count == 0 && persistedReferences.Count == 0 && originOwned.Count == 0)
            return SwiftRebinding.None;

        var symbolicEdges = new HashSet<PendingEdge>(symbolic, ReferenceEqualityComparer.Instance);
        buffer.RemoveEdges(symbolicEdges.Contains);

        var reexpressed = persistedReferences
            .Select(edge => (Stored: edge, Symbolic: ReexpressSwiftEdge(edge)))
            .ToList();

        // One batched lookup for every name any reference needs, plus the affected names' stubs.
        var paths = symbolic.Concat(reexpressed.Select(item => item.Symbolic))
            .SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
            .Select(qn => SwiftTypeReference.TryParse(qn, out var path) ? path : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var lookupNames = paths.Select(SwiftTypeReference.SimpleName)
            .Concat(affectedNames)
            .ToHashSet(StringComparer.Ordinal);
        var persistedByName = includePersistedNodes && lookupNames.Count > 0
            ? (await _store.FindNodesByNamesAsync(projectName, lookupNames, ct))
                .Where(node => !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath ?? "")))
                .ToList()
            : [];

        var resolutions = new Dictionary<string, IReadOnlyList<GraphNode>>(StringComparer.Ordinal);
        IReadOnlyList<GraphNode> Resolve(string typePath)
        {
            if (resolutions.TryGetValue(typePath, out var cached))
                return cached;

            var simpleName = SwiftTypeReference.SimpleName(typePath);
            cached = buffer.FindByName(simpleName)
                .Concat(persistedByName.Where(node => string.Equals(node.Name, simpleName, StringComparison.Ordinal)))
                .Where(IsSwiftTypeNode)
                .Where(node => SwiftTypeReference.TypePath(projectName, node.FilePath, node.QualifiedName) == typePath)
                .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
                .OrderBy(node => node.QualifiedName, StringComparer.Ordinal)
                .ToList();
            resolutions[typePath] = cached;
            return cached;
        }

        // External stubs: a protocol when anything conforms to or refines it, else a class.
        var stubs = new Dictionary<string, (string Path, NodeLabel Label)>(StringComparer.Ordinal);
        string Stub(string typePath, bool isProtocol)
        {
            var qualifiedName = SwiftTypeReference.ExternalQualifiedName(typePath);
            var label = isProtocol || (stubs.TryGetValue(qualifiedName, out var existing)
                                       && existing.Label == NodeLabel.Interface)
                ? NodeLabel.Interface
                : NodeLabel.Class;
            stubs[qualifiedName] = (typePath, label);
            return qualifiedName;
        }

        // An endpoint binds to one repository type, to a stub when there is none, and — when a
        // relation names an ambiguous type — to every candidate with low confidence, so the
        // reference stays recorded and is re-bound once the name becomes unique. Members of an
        // extension of an ambiguous type stay under the extension's file instead.
        var ambiguous = new SortedSet<string>(StringComparer.Ordinal);
        List<PendingEdge> ResolveEdge(PendingEdge edge)
        {
            var properties = edge.Properties is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(edge.Properties);
            // A previously ambiguous binding starts over from the extractor's certainty.
            if (properties.Remove(SwiftTypeReference.AmbiguousProperty))
                properties["confidence"] = "high";
            var isDefinition = edge.Type is EdgeType.DEFINES or EdgeType.DEFINES_METHOD;

            List<GraphNode?> sources = [null];
            var sourceQN = edge.SourceQN;
            if (SwiftTypeReference.TryParse(sourceQN, out var sourcePath))
            {
                properties[SwiftTypeReference.SourceRefProperty] = sourceQN;
                properties[SwiftTypeReference.SourceNameProperty] = SwiftTypeReference.SimpleName(sourcePath);
                var candidates = Resolve(sourcePath);
                if (candidates.Count == 0)
                {
                    sourceQN = Stub(sourcePath, isProtocol: false);
                }
                else if (candidates.Count == 1 || !isDefinition)
                {
                    sources = [.. candidates];
                }
                else if (PlainString(properties, SwiftTypeReference.OriginFileProperty) is { Length: > 0 } originFile)
                {
                    ambiguous.Add(sourcePath);
                    sourceQN = $"{projectName}:{originFile}";
                }
                else
                {
                    ambiguous.Add(sourcePath);
                    return [];
                }

                if (candidates.Count > 1 && !isDefinition)
                    ambiguous.Add(sourcePath);
            }

            List<GraphNode?> targets = [null];
            var targetQN = edge.TargetQN;
            var targetIsReference = SwiftTypeReference.TryParse(targetQN, out var targetPath);
            string? inferredKind = null;
            if (targetIsReference)
            {
                properties[SwiftTypeReference.TargetRefProperty] = targetQN;
                properties[SwiftTypeReference.TargetNameProperty] = SwiftTypeReference.SimpleName(targetPath);
                var candidates = Resolve(targetPath);
                if (candidates.Count > 0)
                    targets = [.. candidates];
                if (candidates.Count > 1)
                    ambiguous.Add(targetPath);

                // A guessed superclass-or-protocol kind holds only while the target is external.
                inferredKind = PlainString(properties, SwiftTypeReference.InferredKindProperty);
                if (inferredKind is null && PlainBool(properties, SwiftTypeReference.InheritanceInferredProperty))
                    properties[SwiftTypeReference.InferredKindProperty] = inferredKind = edge.Type.ToString();
            }

            var fannedOut = sources.Count > 1 || targets.Count > 1;
            var result = new List<PendingEdge>();
            foreach (var source in sources)
            {
                foreach (var target in targets)
                {
                    var edgeProperties = new Dictionary<string, object>(properties);
                    var type = edge.Type;
                    var resolvedTargetQN = targetQN;
                    if (targetIsReference)
                    {
                        if (inferredKind is not null && target is not null)
                        {
                            type = target.Label == NodeLabel.Interface ? EdgeType.IMPLEMENTS : EdgeType.INHERITS;
                            edgeProperties.Remove(SwiftTypeReference.InheritanceInferredProperty);
                            edgeProperties["confidence"] = "high";
                        }
                        else if (inferredKind is not null)
                        {
                            type = Enum.Parse<EdgeType>(inferredKind);
                            edgeProperties[SwiftTypeReference.InheritanceInferredProperty] = true;
                            edgeProperties["confidence"] = "medium";
                        }

                        resolvedTargetQN = target?.QualifiedName ?? Stub(targetPath, isProtocol:
                            type == EdgeType.IMPLEMENTS
                            || PlainBool(edgeProperties, SwiftTypeReference.ProtocolTargetProperty));
                    }

                    if (fannedOut)
                    {
                        edgeProperties[SwiftTypeReference.AmbiguousProperty] = true;
                        edgeProperties["confidence"] = "low";
                    }

                    result.Add(new PendingEdge(source?.QualifiedName ?? sourceQN, resolvedTargetQN, type, edgeProperties));
                }
            }

            return result;
        }

        foreach (var edge in symbolic)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var resolved in ResolveEdge(edge))
                buffer.AddEdge(resolved);
        }

        var rebound = new Dictionary<string, PendingEdge>(StringComparer.Ordinal);
        var retracted = originOwned.Select(ToPendingEdge).ToList();
        foreach (var (stored, reference) in reexpressed)
        {
            ct.ThrowIfCancellationRequested();
            var storedKey = StoredEdgeKey(stored);
            var sourceRecreated = excludedPersistedFiles.Contains(NormalizeRelativePath(stored.SourceFilePath ?? ""));
            var resolved = ResolveEdge(reference);
            if (!resolved.Any(edge => EdgeKey(edge) == storedKey))
                retracted.Add(ToPendingEdge(stored));

            foreach (var edge in resolved)
            {
                // An unchanged edge is re-sent only when this run re-creates its source node.
                if (EdgeKey(edge) == storedKey && !sourceRecreated && SameSwiftBinding(edge, stored))
                    continue;
                rebound.TryAdd(EdgeKey(edge), edge);
            }
        }

        foreach (var edge in rebound.Values)
            buffer.AddEdge(edge);

        foreach (var (qualifiedName, (typePath, label)) in stubs)
        {
            if (buffer.FindByQN(qualifiedName) is not null)
                continue;
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = label,
                Name = SwiftTypeReference.SimpleName(typePath),
                QualifiedName = qualifiedName,
                Properties = new() { ["stub"] = true, ["language"] = "Swift", ["type_path"] = typePath }
            });
        }

        // Stubs that may have lost their last reference: the affected names' stubs and the
        // stub ends of retracted edges.
        var orphanCandidates = persistedByName
            .Where(node => SwiftTypeReference.IsExternal(node.QualifiedName) && affectedNames.Contains(node.Name))
            .Select(node => node.QualifiedName)
            .Concat(retracted.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
                .Where(SwiftTypeReference.IsExternal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ambiguous.Count > 0)
        {
            _logger.LogWarning(
                "Swift type references to {Count} ambiguous type path(s) were not bound (members stay under their file, relations are dropped): {Paths}",
                ambiguous.Count, string.Join(", ", ambiguous.Take(MaxLoggedAmbiguousSwiftTypes)));
        }

        _logger.LogInformation(
            "Swift type references: {Symbolic} resolved, {Rebound} persisted edge(s) re-bound, {Retracted} retracted, {Stubs} external stub(s)",
            symbolic.Count, rebound.Count, retracted.Count, stubs.Count);

        return new SwiftRebinding(rebound.Values.ToList(), new GraphSliceRetractions(retracted, orphanCandidates));
    }

    /// <summary>A persisted Swift edge with its symbolic endpoints and guessed kind restored.</summary>
    private static PendingEdge ReexpressSwiftEdge(StoredEdge edge)
    {
        var properties = ToPlainProperties(edge.Properties);
        var sourceQN = PlainString(properties, SwiftTypeReference.SourceRefProperty) ?? edge.SourceQN;
        var targetQN = PlainString(properties, SwiftTypeReference.TargetRefProperty) ?? edge.TargetQN;
        return new PendingEdge(sourceQN, targetQN, edge.Type, properties);
    }

    private static PendingEdge ToPendingEdge(StoredEdge edge) =>
        new(edge.SourceQN, edge.TargetQN, edge.Type, ToPlainProperties(edge.Properties));

    private static string StoredEdgeKey(StoredEdge edge) =>
        $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";

    /// <summary>Whether a re-resolved edge carries the same binding qualifiers as its stored form.</summary>
    private static bool SameSwiftBinding(PendingEdge resolved, StoredEdge stored) =>
        PlainBool(resolved.Properties, SwiftTypeReference.AmbiguousProperty)
            == PlainBool(stored.Properties, SwiftTypeReference.AmbiguousProperty)
        && PlainBool(resolved.Properties, SwiftTypeReference.InheritanceInferredProperty)
            == PlainBool(stored.Properties, SwiftTypeReference.InheritanceInferredProperty)
        && PlainString(resolved.Properties, "confidence") == PlainString(stored.Properties, "confidence");

    private static string EdgeKey(PendingEdge edge) =>
        $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";

    private static string? PlainString(IReadOnlyDictionary<string, object>? properties, string key) =>
        properties?.GetValueOrDefault(key) switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement json => json.ToString(),
            var value => value.ToString()
        };

    private static bool PlainBool(IReadOnlyDictionary<string, object>? properties, string key) =>
        properties?.GetValueOrDefault(key) switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };

    /// <summary>Edge properties read back from a store, with JSON values unwrapped.</summary>
    private static Dictionary<string, object> ToPlainProperties(IReadOnlyDictionary<string, object>? properties)
    {
        var plain = new Dictionary<string, object>(StringComparer.Ordinal);
        if (properties is null)
            return plain;

        foreach (var (key, value) in properties)
        {
            object? unwrapped = value is JsonElement json
                ? json.ValueKind switch
                {
                    JsonValueKind.String => json.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => json.TryGetInt64(out var integer) ? integer : json.GetDouble(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => json.ToString()
                }
                : value;
            if (unwrapped is not null)
                plain[key] = unwrapped;
        }

        return plain;
    }

    /// <summary>
    /// Records an overlay's Swift re-binding in its slice: each retracted default-branch edge as an
    /// edge marked <see cref="Overlays.OverlayGraphView.RetractedProperty"/>, and each stub that is
    /// left without any edge at the overlay head as a node marked the same way. The overlay view
    /// hides both.
    /// </summary>
    private async Task AddOverlayRetractionsAsync(
        string projectName,
        SwiftRebinding rebinding,
        List<GraphNode> nodes,
        List<PendingEdge> edges,
        CancellationToken ct)
    {
        var retracted = rebinding.Retractions.Edges;
        var retractedKeys = retracted
            .Select(edge => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}")
            .ToHashSet(StringComparer.Ordinal);
        var referenced = edges.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
            .ToHashSet(StringComparer.Ordinal);
        edges.AddRange(retracted.Select(edge => new PendingEdge(
            edge.SourceQN, edge.TargetQN, edge.Type,
            new Dictionary<string, object> { [Overlays.OverlayGraphView.RetractedProperty] = true })));

        var candidates = rebinding.Retractions.OrphanCandidateQNs
            .Where(qualifiedName => !referenced.Contains(qualifiedName))
            .ToList();
        if (candidates.Count == 0)
            return;

        // Every default-branch edge of a stub carries the stub's name as a reference name.
        var names = candidates
            .Select(qualifiedName => SwiftTypeReference.SimpleName(qualifiedName[SwiftTypeReference.ExternalPrefix.Length..]))
            .ToHashSet(StringComparer.Ordinal);
        var baseEdges = (await _store.FindEdgesByPropertyAsync(
                projectName, SwiftTypeReference.SourceNameProperty, names, ct))
            .Concat(await _store.FindEdgesByPropertyAsync(
                projectName, SwiftTypeReference.TargetNameProperty, names, ct))
            .ToList();
        foreach (var candidate in candidates)
        {
            var survives = baseEdges.Any(edge =>
                (edge.SourceQN == candidate || edge.TargetQN == candidate)
                && !retractedKeys.Contains(StoredEdgeKey(edge)));
            if (survives)
                continue;

            var typePath = candidate[SwiftTypeReference.ExternalPrefix.Length..];
            nodes.Add(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.Class,
                Name = SwiftTypeReference.SimpleName(typePath),
                QualifiedName = candidate,
                Properties = new() { [Overlays.OverlayGraphView.RetractedProperty] = true }
            });
        }
    }

    /// <summary>
    /// Adds to a file slice the Swift edges its endpoint-based selection misses: re-bound edges,
    /// and edges emitted by one of <paramref name="replacedPaths"/> whose endpoints both lie
    /// elsewhere (e.g. an extension's conformance of an external type). Their file-less endpoint
    /// nodes (stubs) come along.
    /// </summary>
    private static void AppendSwiftEdges(
        GraphBuffer buffer,
        SwiftRebinding rebinding,
        IReadOnlySet<string> replacedPaths,
        List<GraphNode> nodes,
        List<PendingEdge> edges)
    {
        var present = edges
            .Select(edge => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}")
            .ToHashSet(StringComparer.Ordinal);
        var extra = rebinding.ReboundEdges
            .Concat(buffer.AllPendingEdges.Where(edge =>
                PlainString(edge.Properties, SwiftTypeReference.OriginFileProperty) is { Length: > 0 } origin
                && replacedPaths.Contains(NormalizeRelativePath(origin))))
            .Where(edge => present.Add($"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}"))
            .ToList();
        if (extra.Count == 0)
            return;

        edges.AddRange(extra);
        var presentNodes = nodes.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        foreach (var qualifiedName in extra.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }))
        {
            if (buffer.FindByQN(qualifiedName) is { } node
                && string.IsNullOrWhiteSpace(node.FilePath)
                && presentNodes.Add(qualifiedName))
            {
                nodes.Add(node);
            }
        }
    }
}
