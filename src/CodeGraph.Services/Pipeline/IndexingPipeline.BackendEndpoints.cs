using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private const string EndpointOwner = "http_endpoints";

    // Registration analysis runs independently of the definition tier: SCIP and Tree-sitter
    // supply the same existing callable graph, while this pass only adds routes and handlers.
    private async Task<IReadOnlyList<string>> PrepareBackendEndpointsAsync(
        string repository, string root, ExtractorContext context, GraphBuffer buffer,
        HashSet<string> processed, IReadOnlyCollection<string> deleted, bool incremental, CancellationToken ct)
    {
        var storedRoutes = incremental
            ? (await _store.FindNodesByLabelAsync(repository, NodeLabel.Route, int.MaxValue))
                .Where(node => TypeScriptNodeText(node.Properties.GetValueOrDefault("semantic_owner")) == EndpointOwner).ToList()
            : [];
        var oldPaths = storedRoutes.Where(node => node.FilePath is not null)
            .Select(node => NormalizeRelativePath(node.FilePath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registrations = new List<(string Path, string Content, IBackendEndpointExtractor Extractor)>();
        foreach (var fullPath in DiscoverPaths(root, "**/*.rs", "**/*.go"))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
            if (_extensions.ExtractorFor(Path.GetExtension(fullPath)) is not IBackendEndpointExtractor extractor)
            {
                if (oldPaths.Contains(relative)) throw new InvalidOperationException($"Backend registration extractor unavailable for {relative}; retaining prior graph.");
                continue;
            }
            var source = _fileSystem.ReadAllText(fullPath);
            if (oldPaths.Contains(relative) || extractor.MayContainBackendEndpoints(Path.GetExtension(fullPath), source))
                registrations.Add((fullPath, source, extractor));
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var preliminary = await registration.Extractor.ExtractBackendEndpointsAsync(registration.Path, registration.Content, context, [], ct);
            if (!preliminary.Succeeded || !preliminary.Complete)
                throw new InvalidOperationException($"Backend registration analysis incomplete for {registration.Path}; retaining prior graph.");
            foreach (var route in preliminary.Nodes)
                if (TypeScriptNodeText(route.Properties.GetValueOrDefault("handler_name")) is { Length: > 0 } name) names.Add(name);
        }
        var excluded = processed.Concat(deleted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = buffer.AllNodes.ToDictionary(node => node.QualifiedName, StringComparer.Ordinal);
        if (incremental)
            foreach (var batch in names.Chunk(500))
                foreach (var node in await _store.FindNodesByNamesAsync(repository, batch, ct))
                    if (node.FilePath is null || !excluded.Contains(NormalizeRelativePath(node.FilePath)))
                        available.TryAdd(node.QualifiedName, node);
        var nodes = new List<GraphNode>();
        var edges = new List<PendingEdge>();
        foreach (var registration in registrations)
        {
            var result = await registration.Extractor.ExtractBackendEndpointsAsync(registration.Path, registration.Content, context, available.Values.ToList(), ct);
            if (!result.Succeeded || !result.Complete)
                throw new InvalidOperationException($"Backend registration analysis incomplete for {registration.Path}; retaining prior graph.");
            nodes.AddRange(result.Nodes);
            edges.AddRange(result.Edges);
        }
        // The legacy backend identity is method+path. Conflicting registrations across apps/files
        // cannot overwrite one another and masquerade as a single proven endpoint.
        foreach (var group in nodes.GroupBy(node => node.QualifiedName).Where(group => group.Select(node => node.FilePath).Distinct().Count() > 1).ToList())
        {
            var first = group.OrderBy(node => node.FilePath, StringComparer.Ordinal).First();
            var properties = new Dictionary<string, object>(first.Properties)
            {
                ["registration_ambiguous"] = true,
                ["registration_sources"] = group.Select(node => node.FilePath).Distinct().Order(StringComparer.Ordinal).ToArray()
            };
            nodes.RemoveAll(node => node.QualifiedName == group.Key);
            nodes.Add(first with { Properties = properties });
            edges.RemoveAll(edge => edge.SourceQN == group.Key);
        }
        var storedEdges = new List<PendingEdge>();
        foreach (var batch in storedRoutes.Select(node => node.Id).Chunk(500))
        {
            var rows = await _store.FindEdgesBySourceBatchAsync(batch, [EdgeType.HANDLES]);
            var targetIds = rows.Select(edge => edge.TargetId).Distinct().ToArray();
            var targets = await _store.FindNodesByIdBatchAsync(targetIds);
            var sources = storedRoutes.ToDictionary(node => node.Id);
            storedEdges.AddRange(rows.Where(edge => sources.ContainsKey(edge.SourceId) && targets.ContainsKey(edge.TargetId))
                .Select(edge => new PendingEdge(sources[edge.SourceId].QualifiedName, targets[edge.TargetId].QualifiedName, edge.Type, edge.Properties)));
        }
        var before = Signatures(repository, storedRoutes, storedEdges);
        var after = Signatures(repository, nodes, edges);
        var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !before.TryGetValue(path, out var previous) || !after.TryGetValue(path, out var next) || !previous.SetEquals(next))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (incremental)
            foreach (var batch in changed.Where(path => !processed.Contains(path) && !deleted.Contains(path)).Chunk(100))
            {
                // A registration or its target can change without changing this source's bytes.
                // Retain unrelated definitions and outgoing relationships in its replaced slice.
                var previous = await _store.FindNodesByFilesAsync(repository, batch, ct);
                var retained = previous.Where(node => TypeScriptNodeText(node.Properties.GetValueOrDefault("semantic_owner")) != EndpointOwner).ToList();
                foreach (var node in retained)
                    if (buffer.FindByQN(node.QualifiedName) is null) buffer.AddNode(node);
                var known = retained.ToDictionary(node => node.Id);
                foreach (var sourceBatch in retained.Select(node => node.Id).Chunk(500))
                {
                    var outgoing = await _store.FindEdgesBySourceBatchAsync(sourceBatch);
                    var targets = await _store.FindNodesByIdBatchAsync(outgoing.Select(edge => edge.TargetId).Distinct().ToArray());
                    foreach (var edge in outgoing)
                        if (known.TryGetValue(edge.SourceId, out var source) && targets.TryGetValue(edge.TargetId, out var target)
                            && !deleted.Contains(target.FilePath ?? ""))
                            buffer.AddEdge(new PendingEdge(source.QualifiedName, target.QualifiedName, edge.Type, edge.Properties));
                }
                foreach (var relative in batch)
                {
                    var full = Path.Combine(root, relative);
                    if (_fileSystem.FileExists(full))
                    {
                        buffer.AddFileHash(relative, Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(_fileSystem.ReadAllBytes(full))));
                        processed.Add(relative);
                    }
                }
            }
        foreach (var node in nodes) buffer.AddNode(node);
        foreach (var edge in edges) buffer.AddEdge(edge);
        _logger.LogInformation("Backend registrations of {Repository}: {Routes} routes; {Slices} changed slices", repository, nodes.Count, changed.Count);
        return changed.ToList();
    }
}
