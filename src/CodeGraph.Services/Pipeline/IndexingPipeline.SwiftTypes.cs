using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Cross-file resolution of Swift type references (see <see cref="SwiftTypeReference"/>).
/// </summary>
public partial class IndexingPipeline
{
    private static readonly EdgeType[] SwiftExtensionEdgeTypes =
        [EdgeType.DEFINES, EdgeType.DEFINES_METHOD, EdgeType.IMPLEMENTS, EdgeType.INHERITS];

    private static bool IsSwiftTypeLabel(NodeLabel label) =>
        label is NodeLabel.Class or NodeLabel.Interface or NodeLabel.Struct or NodeLabel.Enum;

    private static bool IsSwiftTypeNode(GraphNode node) =>
        IsSwiftTypeLabel(node.Label)
        && node.FilePath?.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) == true;

    private enum SwiftTypeOutcome { Resolved, Missing, Ambiguous }

    private sealed record SwiftTypeResolution(SwiftTypeOutcome Outcome, GraphNode? Node);

    /// <summary>
    /// Resolves the symbolic Swift type endpoints the extractor emitted: the extended type of a
    /// cross-file extension (its members' definition edges and conformances start there) and
    /// supertypes not declared in the referring file. A name matching exactly one Swift type in the
    /// repository (this run's files, else the persisted graph outside the re-indexed files) binds
    /// to it, and a class's inferred first supertype takes that type's kind: IMPLEMENTS for a
    /// protocol, INHERITS otherwise. A name matching no repository type is external and binds to a
    /// stub node. An ambiguous extended type leaves its members under the extension's file; an
    /// ambiguous supertype yields no edge.
    /// </summary>
    /// <remarks>
    /// On an incremental run, extension edges whose source type was declared in a re-indexed file
    /// but whose extension lives in an unchanged file would be lost with the type's old node; they
    /// are read back from the persisted graph and resolved again, so a moved or deleted type is
    /// handled like any other reference.
    /// </remarks>
    internal async Task ResolveSwiftTypeReferencesAsync(
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
        var carried = includePersistedNodes
            ? await ReadCarriedSwiftExtensionEdgesAsync(projectName, excludedPersistedFiles, ct)
            : [];
        if (symbolic.Count == 0 && carried.Count == 0)
            return;

        var symbolicEdges = new HashSet<PendingEdge>(symbolic, ReferenceEqualityComparer.Instance);
        buffer.RemoveEdges(symbolicEdges.Contains);

        var resolutions = new Dictionary<string, SwiftTypeResolution>(StringComparer.Ordinal);
        async Task<SwiftTypeResolution> ResolveAsync(string typePath)
        {
            if (resolutions.TryGetValue(typePath, out var cached))
                return cached;

            var simpleName = SwiftTypeReference.SimpleName(typePath);
            var candidates = buffer.FindByName(simpleName).AsEnumerable();
            if (includePersistedNodes)
            {
                var persisted = await _store.FindNodesByNameAsync(projectName, simpleName);
                candidates = candidates.Concat(persisted.Where(node =>
                    !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath ?? ""))));
            }

            // A qualified path (Outer.Inner) only matches the type nested at that path.
            var nestedSuffix = "#type:" + typePath.Replace(".", "#type:", StringComparison.Ordinal);
            var matches = candidates
                .Where(IsSwiftTypeNode)
                .Where(node => !typePath.Contains('.', StringComparison.Ordinal)
                               || node.QualifiedName.EndsWith(nestedSuffix, StringComparison.Ordinal))
                .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
                .ToList();

            cached = matches.Count switch
            {
                0 => new SwiftTypeResolution(SwiftTypeOutcome.Missing, null),
                1 => new SwiftTypeResolution(SwiftTypeOutcome.Resolved, matches[0]),
                _ => new SwiftTypeResolution(SwiftTypeOutcome.Ambiguous, null)
            };
            resolutions[typePath] = cached;
            return cached;
        }

        // External stubs: a protocol when anything conforms to or refines it, else a class.
        var stubs = new Dictionary<string, (string Path, NodeLabel Label)>(StringComparer.Ordinal);
        string Stub(string typePath, bool isProtocol)
        {
            var qualifiedName = SwiftTypeReference.ExternalQualifiedName(typePath);
            if (!stubs.TryGetValue(qualifiedName, out var stub) || isProtocol)
                stubs[qualifiedName] = (typePath, isProtocol || stub.Label == NodeLabel.Interface
                    ? NodeLabel.Interface
                    : NodeLabel.Class);
            return qualifiedName;
        }

        var dropped = 0;
        foreach (var edge in symbolic.Concat(carried))
        {
            ct.ThrowIfCancellationRequested();
            var properties = edge.Properties is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(edge.Properties);
            var type = edge.Type;
            var isDefinition = type is EdgeType.DEFINES or EdgeType.DEFINES_METHOD;

            var sourceQN = edge.SourceQN;
            if (SwiftTypeReference.TryParse(sourceQN, out var sourcePath))
            {
                var source = await ResolveAsync(sourcePath);
                if (source.Outcome == SwiftTypeOutcome.Resolved)
                {
                    sourceQN = source.Node!.QualifiedName;
                }
                else if (source.Outcome == SwiftTypeOutcome.Missing)
                {
                    sourceQN = Stub(sourcePath, isProtocol: false);
                }
                else if (isDefinition
                         && properties.GetValueOrDefault(SwiftTypeReference.ExtensionFileProperty)?.ToString()
                             is { Length: > 0 } extensionFile)
                {
                    sourceQN = $"{projectName}:{extensionFile}";
                }
                else
                {
                    dropped++;
                    continue;
                }
            }

            var targetQN = edge.TargetQN;
            if (SwiftTypeReference.TryParse(targetQN, out var targetPath))
            {
                var target = await ResolveAsync(targetPath);
                if (target.Outcome == SwiftTypeOutcome.Ambiguous)
                {
                    dropped++;
                    continue;
                }

                // The repository type's kind replaces a guessed superclass-or-protocol kind.
                if (target.Outcome == SwiftTypeOutcome.Resolved
                    && properties.Remove(SwiftTypeReference.InheritanceInferredProperty))
                {
                    type = target.Node!.Label == NodeLabel.Interface ? EdgeType.IMPLEMENTS : EdgeType.INHERITS;
                    properties["confidence"] = "high";
                }

                targetQN = target.Outcome == SwiftTypeOutcome.Resolved
                    ? target.Node!.QualifiedName
                    : Stub(targetPath, isProtocol: type == EdgeType.IMPLEMENTS
                        || buffer.FindByQN(sourceQN)?.Label == NodeLabel.Interface);
            }

            buffer.AddEdge(new PendingEdge(sourceQN, targetQN, type, properties));
        }

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

        _logger.LogDebug(
            "Swift type references: {Resolved} edges resolved ({Carried} carried forward), {Stubs} external stubs, {Dropped} ambiguous dropped",
            symbolic.Count + carried.Count - dropped, carried.Count, stubs.Count, dropped);
    }

    /// <summary>
    /// Extension edges persisted from a Swift type declared in a re-indexed file to members or
    /// conformances of an extension in an unchanged file, re-expressed with their symbolic source.
    /// </summary>
    private async Task<IReadOnlyList<PendingEdge>> ReadCarriedSwiftExtensionEdgesAsync(
        string projectName,
        IReadOnlySet<string> excludedPersistedFiles,
        CancellationToken ct)
    {
        var typeIds = new List<long>();
        foreach (var file in excludedPersistedFiles.Where(file =>
                     file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            typeIds.AddRange((await _store.FindNodesByFileAsync(projectName, file))
                .Where(node => IsSwiftTypeLabel(node.Label))
                .Select(node => node.Id));
        }

        if (typeIds.Count == 0)
            return [];

        var edges = (await _store.FindEdgesBySourceBatchAsync(typeIds, SwiftExtensionEdgeTypes))
            .Select(edge => (Edge: edge, Properties: ToPlainProperties(edge.Properties)))
            .Where(item =>
                SwiftTypeReference.TryParse(
                    item.Properties.GetValueOrDefault(SwiftTypeReference.OwnerReferenceProperty)?.ToString(), out _)
                && item.Properties.GetValueOrDefault(SwiftTypeReference.ExtensionFileProperty)?.ToString()
                    is { Length: > 0 } extensionFile
                && !excludedPersistedFiles.Contains(NormalizeRelativePath(extensionFile)))
            .ToList();
        if (edges.Count == 0)
            return [];

        var targets = await _store.FindNodesByIdBatchAsync(
            edges.Select(item => item.Edge.TargetId).Distinct().ToList());
        return edges
            .Where(item => targets.ContainsKey(item.Edge.TargetId))
            .Select(item => new PendingEdge(
                item.Properties[SwiftTypeReference.OwnerReferenceProperty].ToString()!,
                targets[item.Edge.TargetId].QualifiedName,
                item.Edge.Type,
                item.Properties))
            .ToList();
    }

    /// <summary>Edge properties read back from a store, with JSON values unwrapped.</summary>
    private static Dictionary<string, object> ToPlainProperties(Dictionary<string, object>? properties)
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
}
