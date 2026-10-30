using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

public partial class CrossRepoLinker
{
    /// <summary>
    /// A target-only index can change global path uniqueness for callers whose source files did
    /// not run. Use the linker's existing compact projections to locate changed local bindings,
    /// then reconcile only those caller repositories against their persisted successful slices.
    /// </summary>
    private async Task<int> ReconcileLocalHttpBindingsAsync(string? scopeRepository,
        IReadOnlyList<LinkEdgeRow> httpCalls, Dictionary<string, List<LinkNodeRow>> routeLookup,
        HashSet<string> repositoryNames, CancellationToken ct)
    {
        var rawCallers = new HashSet<(string Repository, long Source)>();
        var expected = new Dictionary<string, HashSet<(long Source, string Target)>>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, HashSet<(long Source, string Target)>>(StringComparer.OrdinalIgnoreCase);
        static HashSet<(long, string)> RepositorySet(Dictionary<string, HashSet<(long, string)>> groups, string repository)
        {
            if (!groups.TryGetValue(repository, out var set)) groups[repository] = set = [];
            return set;
        }
        var nextCross = new Dictionary<string, Dictionary<string, CrossRepoEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in httpCalls)
        {
            ct.ThrowIfCancellationRequested();
            if (edge.EdgeProperties.GetValueOrDefault("semantic_owner") == IndexingPipeline.HttpBindingOwner)
            {
                RepositorySet(previous, edge.SourceRepository).Add((edge.SourceId, edge.TargetQualifiedName));
                continue;
            }
            if (GetBoolean(edge.EdgeProperties.GetValueOrDefault("binding_ambiguous")))
            {
                // Explicit ambiguity is negative request evidence, unlike absent output from
                // an incomplete analyzer. It must retract a previously derived relation.
                if (IsMatchableHttpCall(edge.EdgeProperties.GetValueOrDefault("http_method"), edge.EdgeProperties.GetValueOrDefault("url_pattern")))
                    rawCallers.Add((edge.SourceRepository, edge.SourceId));
                continue;
            }
            var requestDto = edge.EdgeProperties.GetValueOrDefault("request_dto");
            if (GetBoolean(edge.EdgeProperties.GetValueOrDefault("gateway_call")) && requestDto is not null && !requestDto.StartsWith("route:"))
            {
                rawCallers.Add((edge.SourceRepository, edge.SourceId));
                var target = ResolveGatewayTargetRepository(edge.EdgeProperties.GetValueOrDefault("service_name"), requestDto, repositoryNames);
                if (target is not null && !target.Equals(edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
                    AddExpected(CreateCrossRepoEdge(edge.SourceRepository, target, edge.SourceId, edge.TargetId,
                        EdgeType.HTTP_CALLS, new() { ["request_dto"] = requestDto, ["gateway_call"] = true, ["confidence_band"] = "medium" }));
                continue;
            }
            var method = edge.EdgeProperties.GetValueOrDefault("http_method");
            var url = edge.EdgeProperties.GetValueOrDefault("url_pattern");
            if (!IsMatchableHttpCall(method, url)) continue;
            rawCallers.Add((edge.SourceRepository, edge.SourceId));
            var candidates = SelectHttpRouteMatches(method!, url!,
                edge.EdgeProperties.GetValueOrDefault("service_name"), edge.EdgeProperties.GetValueOrDefault("request_dto"),
                edge.TargetQualifiedName, CallOrigin(edge.EdgeProperties, url!), repositoryNames, routeLookup);
            if (candidates.Count == 1 && candidates[0].QualifiedName != edge.TargetQualifiedName
                && candidates[0].Repository.Equals(edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
                RepositorySet(expected, edge.SourceRepository).Add((edge.SourceId, candidates[0].QualifiedName));
            if (candidates.Count == 1 && !candidates[0].Repository.Equals(edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
            {
                var route = candidates[0];
                var properties = new Dictionary<string, object>
                {
                    ["http_method"] = method!, ["url_pattern"] = url!,
                    ["route_template"] = route.Properties.GetValueOrDefault("route_template") ?? "", ["confidence_band"] = "medium"
                };
                var origin = CallOrigin(edge.EdgeProperties, url!);
                if (origin is not null) properties["origin"] = origin;
                foreach (var key in new[] { "service_name", "request_dto", "origin_pattern" })
                    if (edge.EdgeProperties.GetValueOrDefault(key) is { Length: > 0 } value) properties[key] = value;
                if (origin is null && properties.ContainsKey("origin_pattern")) properties["origin_unresolved"] = true;
                AddExpected(CreateCrossRepoEdge(edge.SourceRepository, route.Repository, edge.SourceId, route.Id, EdgeType.HTTP_CALLS, properties));
            }
        }

        void AddExpected(CrossRepoEdge edge)
        {
            if (!nextCross.TryGetValue(edge.SourceRepository, out var edges)) nextCross[edge.SourceRepository] = edges = new(StringComparer.Ordinal);
            edges[HttpCrossEdgeKey(edge)] = edge;
        }

        // A derived edge is never itself evidence of a request. In particular, retained
        // legacy/partial output without a raw call must not trigger disappearance guessing.
        foreach (var (repository, bindings) in previous)
            bindings.RemoveWhere(binding => !rawCallers.Contains((repository, binding.Source)));
        var affected = previous.Keys.Concat(expected.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(repository => !(previous.GetValueOrDefault(repository) ?? []).SetEquals(expected.GetValueOrDefault(repository) ?? []))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var repository in affected)
        {
            // The source may have indexed since the compact snapshot. Re-read the authoritative
            // evidence under its ordinary indexing lock and write only an actual binding delta.
            await using var indexingLock = await _store.AcquireRepositoryIndexingLockAsync(repository, ct);
            var binding = await IndexingPipeline.RebindHttpCallsAsync(_store, _logger, repository,
                new GraphBuffer(), includePersistedNodes: true, new HashSet<string>(), ct, preserveBindingsWithoutRawEvidence: true);
            if (binding.ReboundEdges.Count == 0 && binding.Retractions.IsEmpty) continue;
            await _store.ReplaceRepositoryFilesAsync(repository, [IndexingPipeline.HttpBindingSlice], [],
                binding.ReboundEdges, new Dictionary<string, string>(), retractions: binding.Retractions, ct: ct);
            _logger.LogInformation("HTTP target change in {TargetRepository} reconciled local bindings for {CallerRepository}: {Added} added, {Removed} removed",
                scopeRepository ?? "fleet", repository, binding.ReboundEdges.Count, binding.Retractions.Edges.Count);
        }

        // Global ambiguity can also invalidate A→C when only B changed. Diff outgoing HTTP
        // facts for raw-call owners; never clear another link kind or consume derived edges.
        var inserted = 0;
        foreach (var callerRepository in rawCallers.Select(caller => caller.Repository).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var sourceIds = rawCallers.Where(caller => caller.Repository.Equals(callerRepository, StringComparison.OrdinalIgnoreCase))
                .Select(caller => caller.Source).ToHashSet();
            var old = (await _store.FindCrossRepoEdgesAsync(callerRepository, EdgeType.HTTP_CALLS))
                .Where(edge => edge.SourceRepository.Equals(callerRepository, StringComparison.OrdinalIgnoreCase) && sourceIds.Contains(edge.SourceNodeId))
                .ToDictionary(HttpCrossEdgeKey, StringComparer.Ordinal);
            var desired = nextCross.GetValueOrDefault(callerRepository) ?? new(StringComparer.Ordinal);
            var stale = old.Where(pair => !desired.TryGetValue(pair.Key, out var replacement)
                    || HttpCrossProperties(pair.Value) != HttpCrossProperties(replacement)).Select(pair => pair.Value.Id).ToList();
            var added = desired.Where(pair => !old.TryGetValue(pair.Key, out var existing)
                    || HttpCrossProperties(existing) != HttpCrossProperties(pair.Value)).Select(pair => pair.Value).ToList();
            if (stale.Count > 0) await _store.DeleteCrossRepoEdgesByIdsAsync(stale, ct);
            if (added.Count > 0) await _store.InsertCrossRepoEdgeBatchAsync(added, ct);
            inserted += added.Count;
        }
        return inserted;
    }

    private static string HttpCrossEdgeKey(CrossRepoEdge edge) =>
        $"{edge.SourceRepository}\u001f{edge.TargetRepository}\u001f{edge.SourceNodeId}\u001f{edge.TargetNodeId}\u001f{edge.Type}";
    private static string HttpCrossProperties(CrossRepoEdge edge) =>
        JsonSerializer.Serialize(edge.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
}
