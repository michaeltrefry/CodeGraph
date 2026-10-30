using CodeGraph.Data;
using CodeGraph.Models;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    internal const string HttpBindingSlice = ".codegraph/http-bindings";
    internal const string HttpBindingOwner = "http_binding";

    internal sealed record HttpRebinding(IReadOnlyList<PendingEdge> ReboundEdges, GraphSliceRetractions Retractions)
    {
        public static readonly HttpRebinding None = new([], new([], []));
    }

    /// <summary>
    /// Retains raw extraction-owned wildcard calls and derives separately owned edges to eligible
    /// same-repository endpoints. Rechecks persisted calls on backend-only changes without replacing
    /// caller slices. Only the obsolete derived edges are retracted by the graph transaction.
    /// </summary>
    internal Task<HttpRebinding> RebindHttpCallsAsync(string repositoryName, GraphBuffer buffer,
        bool includePersistedNodes, IReadOnlySet<string> excludedPersistedFiles, CancellationToken ct) =>
        RebindHttpCallsAsync(_store, _logger, repositoryName, buffer, includePersistedNodes, excludedPersistedFiles, ct);

    // Shared with post-index target-side reconciliation: source indexing and endpoint changes
    // use the same matching, ambiguity, ownership and meaningful-property comparison rules.
    internal static async Task<HttpRebinding> RebindHttpCallsAsync(IGraphStore store, ILogger logger,
        string repositoryName, GraphBuffer buffer, bool includePersistedNodes,
        IReadOnlySet<string> excludedPersistedFiles, CancellationToken ct, bool preserveBindingsWithoutRawEvidence = false)
    {
        bool Excluded(string? path) => !string.IsNullOrWhiteSpace(path)
            && excludedPersistedFiles.Contains(NormalizeRelativePath(path));
        buffer.RemoveEdges(IsHttpBinding);
        var raw = buffer.AllPendingEdges.Where(IsRawHttpCall).GroupBy(HttpEdgeKey)
            .Where(group => group.Select(HttpMatchingEvidence).Distinct(StringComparer.Ordinal).Take(2).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceFiles = raw.Values.DistinctBy(edge => edge.SourceQN).ToDictionary(edge => edge.SourceQN, edge =>
                buffer.FindByQN(edge.SourceQN)?.FilePath ?? HttpSourceFile(repositoryName, edge.SourceQN), StringComparer.Ordinal);
        var rawSourceEvidence = buffer.AllPendingEdges.Where(edge => edge.Type == EdgeType.HTTP_CALLS && !IsHttpBinding(edge))
            .Select(edge => edge.SourceQN).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<StoredEdge> oldBindings = [];
        if (includePersistedNodes)
        {
            oldBindings = await store.FindEdgesByPropertyAsync(repositoryName, [EdgeType.HTTP_CALLS],
                "semantic_owner", [HttpBindingOwner], ct);
            var persistedRaw = await store.FindEdgesByPropertyAsync(repositoryName, [EdgeType.HTTP_CALLS],
                "http_method", CrossRepoLinker.HttpMethods.SelectMany(method => new[] { method, method.ToLowerInvariant(), method[..1] + method[1..].ToLowerInvariant() }).ToArray(), ct);
            foreach (var edge in persistedRaw)
            {
                if (Excluded(edge.SourceFilePath) || buffer.FindByQN(edge.SourceQN) is not null) continue;
                var pending = new PendingEdge(edge.SourceQN, edge.TargetQN, edge.Type, edge.Properties);
                if (!IsHttpBinding(pending)) rawSourceEvidence.Add(edge.SourceQN);
                if (!IsRawHttpCall(pending)) continue;
                raw.TryAdd(HttpEdgeKey(pending), pending);
                sourceFiles.TryAdd(edge.SourceQN, edge.SourceFilePath ?? "");
            }
        }
        if (raw.Count == 0 && oldBindings.Count == 0) return HttpRebinding.None;

        var routes = buffer.AllNodes.Where(node => node.Label == NodeLabel.Route)
            .DistinctBy(node => node.QualifiedName).ToDictionary(node => node.QualifiedName, StringComparer.Ordinal);
        if (includePersistedNodes)
            foreach (var route in await store.FindNodesByLabelAsync(repositoryName, NodeLabel.Route, int.MaxValue))
                if (!Excluded(route.FilePath)) routes.TryAdd(route.QualifiedName, route);
        var projection = new LinkNodeQuery([NodeLabel.Route]) { PropertyKeys = CrossRepoLinker.HttpRoutePropertyKeys };
        var localLookup = CrossRepoLinker.BuildHttpRouteLookup(routes.Values.Select(route => CrossRepoLinkProjection.ToRow(route, projection)));
        var repositoryNames = (await store.ListRepositoriesAsync()).Select(repository => repository.Name)
            .Append(repositoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<LinkNodeRow>>? globalLookup = null;
        var next = new Dictionary<string, PendingEdge>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var edge in raw.Values)
        {
            ct.ThrowIfCancellationRequested();
            var properties = HttpStrings(edge.Properties);
            var method = properties["http_method"]!;
            var url = properties["url_pattern"]!;
            var service = properties.GetValueOrDefault("service_name");
            var requestDto = properties.GetValueOrDefault("request_dto");
            var hint = CrossRepoLinker.ResolveHttpTargetRepository(service, requestDto, edge.TargetQN, repositoryNames);
            if (hint is not null && !hint.Equals(repositoryName, StringComparison.OrdinalIgnoreCase)) continue;
            var origin = CrossRepoLinker.CallOrigin(properties, url);
            var candidates = CrossRepoLinker.SelectHttpRouteMatches(method, url, service, requestDto, edge.TargetQN,
                origin, repositoryNames, localLookup);
            if (candidates.Count != 1)
            {
                if (unresolved.Count < 10) unresolved.Add($"{method} {url}");
                continue;
            }
            if (hint is null)
            {
                // Without a proven service identity, a matching endpoint in another repository
                // makes a path-only local binding ambiguous. Read compact route keys lazily.
                if (globalLookup is null)
                {
                    var global = routes.Values.Select(route => CrossRepoLinkProjection.ToRow(route, projection)).ToList();
                    await foreach (var streamedRoute in store.StreamLinkNodesAsync(projection, ct).WithCancellation(ct))
                        if (!streamedRoute.Repository.Equals(repositoryName, StringComparison.OrdinalIgnoreCase)) global.Add(streamedRoute);
                    globalLookup = CrossRepoLinker.BuildHttpRouteLookup(global);
                }
                candidates = CrossRepoLinker.SelectHttpRouteMatches(method, url, service, requestDto, edge.TargetQN,
                    origin, repositoryNames, globalLookup);
            }
            if (candidates.Count != 1 || !candidates[0].Repository.Equals(repositoryName, StringComparison.OrdinalIgnoreCase))
            {
                if (unresolved.Count < 10) unresolved.Add($"{method} {url}");
                continue;
            }
            var route = candidates[0];
            // A concrete extraction-owned edge already reaches this endpoint. Deriving an edge
            // with the same graph identity would overwrite the authoritative raw evidence.
            if (edge.TargetQN == route.QualifiedName) continue;
            // Prevent subsequent stub creation from replacing a proven persisted endpoint's
            // buffered identity with a file-less placeholder.
            if (buffer.FindByQN(route.QualifiedName) is null && routes.TryGetValue(route.QualifiedName, out var endpoint))
                buffer.AddNode(endpoint);
            var derived = edge.Properties is null ? new Dictionary<string, object>() : new(edge.Properties);
            derived.Remove("extraction_owner");
            derived.Remove("extraction_source_file");
            derived.Remove("confidence");
            derived["semantic_owner"] = HttpBindingOwner;
            derived["route_template"] = route.Properties.GetValueOrDefault("route_template") ?? "";
            derived["confidence_band"] = "medium";
            if (sourceFiles.TryGetValue(edge.SourceQN, out var sourceFile) && !string.IsNullOrWhiteSpace(sourceFile))
                derived["binding_source_file"] = NormalizeRelativePath(sourceFile);
            if (properties.GetValueOrDefault("origin_pattern") is not null && origin is null)
                derived["origin_unresolved"] = true;
            var binding = new PendingEdge(edge.SourceQN, route.QualifiedName, EdgeType.HTTP_CALLS, derived);
            next[HttpEdgeKey(binding)] = binding;
        }
        if (preserveBindingsWithoutRawEvidence)
        {
            foreach (var old in oldBindings.Where(edge => !rawSourceEvidence.Contains(edge.SourceQN)))
            {
                var retained = new PendingEdge(old.SourceQN, old.TargetQN, old.Type, old.Properties);
                next.TryAdd(HttpEdgeKey(retained), retained);
            }
        }
        foreach (var edge in next.Values) buffer.AddEdge(edge);

        var previous = oldBindings.Select(edge => new PendingEdge(edge.SourceQN, edge.TargetQN, edge.Type, edge.Properties))
            .DistinctBy(HttpEdgeKey).ToDictionary(HttpEdgeKey, StringComparer.Ordinal);
        var retracted = previous.Where(pair => !next.TryGetValue(pair.Key, out var current)
                || SemanticProperties(pair.Value.Properties) != SemanticProperties(current.Properties))
            .Select(pair => pair.Value).ToList();
        var rebound = next.Where(pair => !previous.TryGetValue(pair.Key, out var old)
                || SemanticProperties(old.Properties) != SemanticProperties(pair.Value.Properties)
                || Excluded(sourceFiles.GetValueOrDefault(pair.Value.SourceQN))
                || (routes.TryGetValue(pair.Value.TargetQN, out var route) && Excluded(route.FilePath)))
            .Select(pair => pair.Value).ToList();
        logger.LogInformation("HTTP binding for {Repository}: {Calls} raw call(s), {Bound} local binding(s), {Changed} changed, {Retracted} removed; unresolved samples: {Samples}",
            repositoryName, raw.Count, next.Count, rebound.Count, retracted.Count, string.Join("; ", unresolved));
        return new HttpRebinding(rebound, new GraphSliceRetractions(retracted, []));
    }

    private static string HttpSourceFile(string repository, string sourceQn) =>
        sourceQn.StartsWith($"{repository}:", StringComparison.Ordinal) && IsTypeScriptPath(sourceQn[(repository.Length + 1)..])
            ? sourceQn[(repository.Length + 1)..] : "";
    private static Dictionary<string, string?> HttpStrings(Dictionary<string, object>? properties) =>
        properties?.ToDictionary(pair => pair.Key, pair => TypeScriptNodeText(pair.Value), StringComparer.Ordinal) ?? [];
    private static string HttpEdgeKey(PendingEdge edge) => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";
    private static bool IsHttpBinding(PendingEdge edge) => edge.Type == EdgeType.HTTP_CALLS
        && TypeScriptNodeText(edge.Properties?.GetValueOrDefault("semantic_owner")) == HttpBindingOwner;
    private static string HttpMatchingEvidence(PendingEdge edge)
    {
        var properties = HttpStrings(edge.Properties);
        return string.Join('\u001f', properties.GetValueOrDefault("http_method")?.ToUpperInvariant(),
            properties.GetValueOrDefault("url_pattern"), CrossRepoLinker.CallOrigin(properties, properties.GetValueOrDefault("url_pattern") ?? "")?.ToLowerInvariant(),
            properties.GetValueOrDefault("service_name")?.ToLowerInvariant(), properties.GetValueOrDefault("request_dto"));
    }
    private static bool IsRawHttpCall(PendingEdge edge) => edge.Type == EdgeType.HTTP_CALLS && !IsHttpBinding(edge)
        && !string.Equals(TypeScriptNodeText(edge.Properties?.GetValueOrDefault("binding_ambiguous")), "true", StringComparison.OrdinalIgnoreCase)
        && CrossRepoLinker.IsMatchableHttpCall(TypeScriptNodeText(edge.Properties?.GetValueOrDefault("http_method")),
            TypeScriptNodeText(edge.Properties?.GetValueOrDefault("url_pattern")));

    private static void AppendHttpEdges(GraphBuffer buffer, HttpRebinding rebinding, List<GraphNode> nodes, List<PendingEdge> edges)
    {
        var present = edges.Select(HttpEdgeKey).ToHashSet(StringComparer.Ordinal);
        var added = rebinding.ReboundEdges.Where(edge => present.Add(HttpEdgeKey(edge))).ToList();
        edges.AddRange(added);
        var nodeNames = nodes.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        foreach (var qn in added.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }).Distinct(StringComparer.Ordinal))
            if (buffer.FindByQN(qn) is { } node && string.IsNullOrWhiteSpace(node.FilePath) && nodeNames.Add(qn)) nodes.Add(node);
    }
}
