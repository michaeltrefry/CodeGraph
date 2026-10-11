using System.Runtime.CompilerServices;
using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Selects nodes for cross-repo linking. Only the listed property keys are read; the rest of a
/// node's properties (documentation, signatures, ...) never leave the store.
/// </summary>
public sealed record LinkNodeQuery(IReadOnlyList<NodeLabel> Labels)
{
    /// <summary>Property values each returned node must have (compared case-insensitively).</summary>
    public IReadOnlyDictionary<string, string> PropertyEquals { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Property keys copied into <see cref="LinkNodeRow.Properties"/>.</summary>
    public IReadOnlyList<string> PropertyKeys { get; init; } = [];
}

public sealed record LinkNodeRow(
    long Id,
    string Project,
    NodeLabel Label,
    string Name,
    string QualifiedName,
    IReadOnlyDictionary<string, string?> Properties);

/// <summary>
/// Selects edges for cross-repo linking together with the few endpoint fields the linker needs,
/// so the linker never materializes whole edge or node rows.
/// </summary>
public sealed record LinkEdgeQuery(IReadOnlyList<EdgeType> Types)
{
    /// <summary>When set, only edges whose target node has one of these labels.</summary>
    public IReadOnlyList<NodeLabel>? TargetLabels { get; init; }

    /// <summary>Property values the target node must have (compared case-insensitively).</summary>
    public IReadOnlyDictionary<string, string> TargetPropertyEquals { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Edge property keys copied into <see cref="LinkEdgeRow.EdgeProperties"/>.</summary>
    public IReadOnlyList<string> EdgePropertyKeys { get; init; } = [];

    /// <summary>Target node property keys copied into <see cref="LinkEdgeRow.TargetProperties"/>.</summary>
    public IReadOnlyList<string> TargetPropertyKeys { get; init; } = [];
}

/// <summary>
/// An edge with its endpoints' linking fields. When the query has no target filter and the target
/// node does not exist, the target fields are empty (<see cref="TargetLabel"/> is null).
/// </summary>
public sealed record LinkEdgeRow(
    long SourceId,
    string SourceProject,
    long TargetId,
    string TargetProject,
    NodeLabel? TargetLabel,
    string TargetName,
    string TargetQualifiedName,
    EdgeType Type,
    IReadOnlyDictionary<string, string?> EdgeProperties,
    IReadOnlyDictionary<string, string?> TargetProperties);

/// <summary>
/// Shared filtering/projection rules for stores that answer link queries from
/// <see cref="GraphNode"/>/<see cref="GraphEdge"/> objects rather than SQL.
/// </summary>
public static class CrossRepoLinkProjection
{
    private const int NodeLookupBatchSize = 1000;

    public static bool Matches(
        IReadOnlyDictionary<string, object> properties,
        IReadOnlyDictionary<string, string> propertyEquals)
    {
        foreach (var (key, expected) in propertyEquals)
        {
            if (!string.Equals(ValueOf(properties, key), expected, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static IReadOnlyDictionary<string, string?> Project(
        IReadOnlyDictionary<string, object> properties,
        IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return EmptyProperties;

        var projected = new Dictionary<string, string?>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
            projected[key] = ValueOf(properties, key);
        return projected;
    }

    public static LinkNodeRow ToRow(GraphNode node, LinkNodeQuery query) =>
        new(node.Id, node.Project, node.Label, node.Name, node.QualifiedName,
            Project(node.Properties, query.PropertyKeys));

    public static LinkEdgeRow ToRow(GraphEdge edge, GraphNode source, GraphNode? target, LinkEdgeQuery query) =>
        new(edge.SourceId, source.Project, edge.TargetId, target?.Project ?? "", target?.Label, target?.Name ?? "",
            target?.QualifiedName ?? "", edge.Type,
            Project(edge.Properties, query.EdgePropertyKeys),
            Project(target?.Properties ?? EmptyNodeProperties, query.TargetPropertyKeys));

    public static bool HasTargetFilter(LinkEdgeQuery query) =>
        query.TargetLabels is not null || query.TargetPropertyEquals.Count > 0;

    /// <summary>Whether an edge whose target is <paramref name="target"/> (null when missing) is selected.</summary>
    public static bool TargetMatches(GraphNode? target, LinkEdgeQuery query) =>
        target is null
            ? !HasTargetFilter(query)
            : (query.TargetLabels is null || query.TargetLabels.Contains(target.Label)) &&
              Matches(target.Properties, query.TargetPropertyEquals);

    /// <summary>
    /// Fallback for stores without a native projection query. It reads whole rows a label/type at a
    /// time, so it is only suitable for small graphs (tests, the Neo4j compatibility provider).
    /// </summary>
    public static async IAsyncEnumerable<LinkNodeRow> StreamNodesAsync(
        IGraphStore store,
        LinkNodeQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var label in query.Labels.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var node in await store.FindAllNodesByLabelAsync(label, int.MaxValue))
            {
                if (Matches(node.Properties, query.PropertyEquals))
                    yield return ToRow(node, query);
            }
        }
    }

    /// <inheritdoc cref="StreamNodesAsync"/>
    public static async IAsyncEnumerable<LinkEdgeRow> StreamEdgesAsync(
        IGraphStore store,
        LinkEdgeQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var type in query.Types.Distinct())
        {
            var edges = await store.FindAllEdgesByTypeAsync(type);
            foreach (var batch in edges.Chunk(NodeLookupBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var nodes = await store.FindNodesByIdBatchAsync(
                    batch.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Distinct().ToList());
                foreach (var edge in batch)
                {
                    if (nodes.TryGetValue(edge.SourceId, out var source) &&
                        TargetMatches(nodes.GetValueOrDefault(edge.TargetId), query))
                    {
                        yield return ToRow(edge, source, nodes.GetValueOrDefault(edge.TargetId), query);
                    }
                }
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, object> EmptyNodeProperties =
        new Dictionary<string, object>();

    private static readonly IReadOnlyDictionary<string, string?> EmptyProperties =
        new Dictionary<string, string?>();

    private static string? ValueOf(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;
}
