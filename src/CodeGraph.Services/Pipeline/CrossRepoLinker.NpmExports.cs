using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

public partial class CrossRepoLinker
{
    private const string NpmExportOwner = "npm_exports";
    private static readonly EdgeType[] NpmReferenceTypes = [EdgeType.RENDERS, EdgeType.CALLS, EdgeType.USES_TYPE, EdgeType.INJECTS];
    private static readonly LinkEdgeQuery NpmReferenceQuery = new(NpmReferenceTypes)
    {
        TargetLabels = [NodeLabel.ExternalSymbol],
        TargetPropertyKeys = ["package_name", "import_path", "export_name", "member_path"]
    };
    private readonly record struct NpmExportIdentity(string Package, string ImportPath, string Export, string Member);
    private static bool IsNpmExportLink(CrossRepoEdge edge) =>
        edge.Properties.GetValueOrDefault("semantic_owner")?.ToString() == NpmExportOwner;
    private static NpmExportIdentity? NpmIdentity(IReadOnlyDictionary<string, string?> properties)
    {
        var package = properties.GetValueOrDefault("package_name");
        var import = properties.GetValueOrDefault("import_path");
        var export = properties.GetValueOrDefault("export_name");
        return !string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(import) && !string.IsNullOrWhiteSpace(export)
            && (import == package || import.StartsWith(package + "/", StringComparison.Ordinal))
            ? new(package, import, export, properties.GetValueOrDefault("member_path") ?? "") : null;
    }

    /// <summary>
    /// Resolves explicit npm public-export identities to their actual indexed declarations.
    /// Streaming projections contain only referenced identities and export metadata. All consumers
    /// are compared, because adding/removing a third provider can change an existing A-to-C link.
    /// </summary>
    private async Task<int> LinkNpmExportsAsync(string? scopeRepository, CancellationToken ct)
    {
        var references = new List<(LinkEdgeRow Edge, NpmExportIdentity Identity)>();
        await foreach (var edge in _store.StreamLinkEdgesAsync(NpmReferenceQuery, ct).WithCancellation(ct))
            if (edge.TargetQualifiedName.StartsWith("npm-symbol:", StringComparison.Ordinal)
                && NpmIdentity(edge.TargetProperties) is { } identity) references.Add((edge, identity));
        var wanted = references.Select(reference => reference.Identity).ToHashSet();
        var definitions = new Dictionary<NpmExportIdentity, Dictionary<(string Repository, long Id), LinkNodeRow>>();
        if (wanted.Count > 0)
        {
            await foreach (var node in _store.StreamLinkNodesAsync(new LinkNodeQuery([
                NodeLabel.Component, NodeLabel.Class, NodeLabel.Function, NodeLabel.Method, NodeLabel.Property,
                NodeLabel.Interface, NodeLabel.Enum]) { PropertyKeys = ["npm_exports"] }, ct).WithCancellation(ct))
            {
                var aliases = node.Properties.GetValueOrDefault("npm_exports");
                if (string.IsNullOrWhiteSpace(aliases)) continue;
                try
                {
                    using var json = JsonDocument.Parse(aliases);
                    if (json.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var properties = item.EnumerateObject().ToDictionary(pair => pair.Name,
                            pair => pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() : null);
                        if (NpmIdentity(properties) is not { } identity || !wanted.Contains(identity)) continue;
                        if (!definitions.TryGetValue(identity, out var candidates)) definitions[identity] = candidates = [];
                        candidates[(node.Repository, node.Id)] = node with { Properties = EmptyProperties };
                    }
                }
                catch (JsonException) { _logger.LogWarning("Ignoring invalid npm export metadata on {Repository} {Symbol}", node.Repository, node.QualifiedName); }
            }
        }
        var expected = new Dictionary<(string Source, long SourceId, string Target, long TargetId, EdgeType Type), CrossRepoEdge>();
        var ambiguities = new HashSet<NpmExportIdentity>();
        foreach (var (edge, identity) in references)
        {
            if (!definitions.TryGetValue(identity, out var candidates) || candidates.Count == 0) continue;
            if (candidates.Count != 1) { ambiguities.Add(identity); continue; }
            var target = candidates.Values.Single();
            if (target.Repository.Equals(edge.SourceRepository, StringComparison.OrdinalIgnoreCase)) continue;
            var link = CreateCrossRepoEdge(edge.SourceRepository, target.Repository, edge.SourceId, target.Id, edge.Type,
                new() { ["semantic_owner"] = NpmExportOwner, ["package_name"] = identity.Package,
                    ["import_path"] = identity.ImportPath, ["export_name"] = identity.Export, ["confidence_band"] = "high" });
            if (identity.Member.Length > 0) link.Properties["member_path"] = identity.Member;
            expected[NpmLinkKey(link)] = link;
        }
        var queries = references.Select(reference => (Repository: reference.Edge.SourceRepository, reference.Edge.Type)).ToHashSet();
        if (scopeRepository is not null) foreach (var type in NpmReferenceTypes) queries.Add((scopeRepository, type));
        var stale = new List<long>();
        foreach (var (repository, type) in queries)
        {
            var outgoing = await _store.FindCrossRepoEdgesAsync(repository, type);
            foreach (var old in outgoing.Where(edge => edge.SourceRepository.Equals(repository, StringComparison.OrdinalIgnoreCase) && IsNpmExportLink(edge)))
            {
                var key = NpmLinkKey(old);
                if (expected.TryGetValue(key, out var next) && NpmPropertiesEqual(old.Properties, next.Properties)) expected.Remove(key);
                else stale.Add(old.Id);
            }
        }
        await _store.DeleteCrossRepoEdgesByIdsAsync(stale.Distinct().ToArray(), ct);
        if (expected.Count > 0) await _store.InsertCrossRepoEdgeBatchAsync(expected.Values.ToList(), ct);
        _logger.LogInformation("npm export linking: {References} references, {Added} added, {Removed} removed, {Ambiguous} ambiguous identities; samples: {Samples}",
            references.Count, expected.Count, stale.Count, ambiguities.Count,
            string.Join("; ", ambiguities.Take(10).Select(identity => $"{identity.ImportPath}#{identity.Export}")));
        return expected.Count;
    }

    private static (string Source, long SourceId, string Target, long TargetId, EdgeType Type) NpmLinkKey(CrossRepoEdge edge) =>
        (edge.SourceRepository, edge.SourceNodeId, edge.TargetRepository, edge.TargetNodeId, edge.Type);
    private static bool NpmPropertiesEqual(Dictionary<string, object> left, Dictionary<string, object> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value?.ToString(), value?.ToString(), StringComparison.Ordinal));
}
