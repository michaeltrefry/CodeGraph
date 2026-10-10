using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Joins the results of separately analyzed Rust crate groups. Each group's SCIP index only
/// defines its own crates' symbols, so a reference from crate A to crate B in another group
/// arrives as a <c>scip_external</c> node. This retargets such references to B's definition
/// when one carries the exact same SCIP symbol — the same symbol-identity rule the cross-repo
/// linker uses — and drops external placeholders that nothing references any more.
/// </summary>
public static class RustScipCrossGroupLinker
{
    private const string SourceProperty = "source";
    private const string ScipSource = "scip";
    private const string SymbolProperty = "scip_symbol";
    private const string ExternalProperty = "scip_external";

    public static IReadOnlyList<ExtractionResult> Link(IReadOnlyList<ExtractionResult> results)
    {
        var definitionsBySymbol = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in results.SelectMany(result => result.Nodes))
        {
            if (IsScip(node) && !IsExternal(node) && GetSymbol(node) is { } symbol)
                definitionsBySymbol.TryAdd(symbol, node);
        }

        if (results.Count <= 1 && definitionsBySymbol.Count == 0)
            return results;

        // Every group sees the same std/dependency symbols; keep one external node per QN.
        var emittedExternals = new HashSet<string>(StringComparer.Ordinal);
        var linked = new List<ExtractionResult>(results.Count);
        foreach (var result in results)
        {
            var resolvedExternals = result.Nodes
                .Where(node => IsScip(node) && IsExternal(node))
                .Select(node => (node.QualifiedName, Definition: GetSymbol(node) is { } symbol
                    ? definitionsBySymbol.GetValueOrDefault(symbol)
                    : null))
                .Where(pair => pair.Definition is not null)
                .ToDictionary(pair => pair.QualifiedName, pair => pair.Definition!, StringComparer.Ordinal);
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            var edges = new List<PendingEdge>(result.Edges.Count);
            foreach (var edge in result.Edges)
            {
                var retargeted = resolvedExternals.TryGetValue(edge.TargetQN, out var definition)
                    ? edge with
                    {
                        TargetQN = definition.QualifiedName,
                        Type = RetargetedEdgeType(edge.Type, definition.Label)
                    }
                    : edge;
                if (retargeted.SourceQN == retargeted.TargetQN)
                    continue;
                if (edgeKeys.Add($"{retargeted.SourceQN}\n{retargeted.TargetQN}\n{retargeted.Type}"))
                    edges.Add(retargeted);
            }

            var referenced = edges
                .SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
                .ToHashSet(StringComparer.Ordinal);
            var nodes = result.Nodes
                .Where(node => !resolvedExternals.ContainsKey(node.QualifiedName) ||
                               referenced.Contains(node.QualifiedName))
                .Where(node => !(IsScip(node) && IsExternal(node)) ||
                               emittedExternals.Add(node.QualifiedName))
                .ToList();

            linked.Add(result with { Nodes = nodes, Edges = edges });
        }

        return linked;
    }

    // A reference edge's type was chosen from the external symbol's kind, which SCIP may omit;
    // the definition's label is authoritative. Non-reference edges (IMPLEMENTS) keep their type.
    private static EdgeType RetargetedEdgeType(EdgeType type, NodeLabel definitionLabel) =>
        type is EdgeType.CALLS or EdgeType.USES_TYPE
            ? definitionLabel is NodeLabel.Class or NodeLabel.Struct or NodeLabel.Enum or NodeLabel.Interface
                ? EdgeType.USES_TYPE
                : EdgeType.CALLS
            : type;

    private static bool IsScip(GraphNode node) =>
        node.Properties.TryGetValue(SourceProperty, out var source) &&
        string.Equals(source?.ToString(), ScipSource, StringComparison.Ordinal);

    private static bool IsExternal(GraphNode node) =>
        node.Properties.TryGetValue(ExternalProperty, out var value) &&
        value switch
        {
            bool flag => flag,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };

    private static string? GetSymbol(GraphNode node) =>
        node.Properties.TryGetValue(SymbolProperty, out var symbol) &&
        symbol?.ToString() is { Length: > 0 } text
            ? text
            : null;
}
