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
        IReadOnlyList<LinkEdgeRow> httpCalls, HttpRouteIndex routeLookup,
        HashSet<string> repositoryNames, HttpBindingBudget work, CancellationToken ct)
    {
        var rawCallers = new Dictionary<string,HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        void Raw(string repository,long source)
        {
            work.Text(repository);
            if (!rawCallers.TryGetValue(repository,out var sources)) rawCallers[repository]=sources=[];
            sources.Add(source);
        }
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
            work.Text(edge.SourceRepository,edge.TargetQualifiedName);
            work.Properties(edge.EdgeProperties);
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
                    Raw(edge.SourceRepository,edge.SourceId);
                continue;
            }
            var requestDto = edge.EdgeProperties.GetValueOrDefault("request_dto");
            if (GetBoolean(edge.EdgeProperties.GetValueOrDefault("gateway_call")) && requestDto is not null && !requestDto.StartsWith("route:"))
            {
                Raw(edge.SourceRepository,edge.SourceId);
                var target = ResolveGatewayTargetRepository(edge.EdgeProperties.GetValueOrDefault("service_name"), requestDto, repositoryNames,work);
                if (target is not null && !target.Equals(edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
                    AddExpected(CreateCrossRepoEdge(edge.SourceRepository, target, edge.SourceId, edge.TargetId,
                        EdgeType.HTTP_CALLS, new() { ["request_dto"] = requestDto, ["gateway_call"] = true, ["confidence_band"] = "medium" }));
                continue;
            }
            var method = edge.EdgeProperties.GetValueOrDefault("http_method");
            var url = edge.EdgeProperties.GetValueOrDefault("url_pattern");
            if (!IsMatchableHttpCall(method, url)) continue;
            Raw(edge.SourceRepository,edge.SourceId);
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
            work.Text(edge.SourceRepository,edge.TargetRepository); work.Properties(edge.Properties);
            if (!nextCross.TryGetValue(edge.SourceRepository, out var edges)) nextCross[edge.SourceRepository] = edges = new(StringComparer.Ordinal);
            edges[HttpCrossEdgeKey(edge)] = edge;
        }

        // A derived edge is never evidence of a request. Plan every owner's local
        // and cross-repository delta before any graph mutation can happen.
        foreach (var (repository, bindings) in previous)
        {
            work.Charge(bindings.Count+1);
            bindings.RemoveWhere(binding => !rawCallers.TryGetValue(repository,out var sources) || !sources.Contains(binding.Source));
        }
        var affected = new List<string>();
        foreach (var repository in previous.Keys.Concat(expected.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            work.Text(repository);
            var before=previous.GetValueOrDefault(repository) ?? [];
            var after=expected.GetValueOrDefault(repository) ?? [];
            work.Charge(before.Count+after.Count+1);
            if (!before.SetEquals(after)) affected.Add(repository);
        }
        var localPlans=new List<(string Repository,IndexingPipeline.HttpRebinding Binding)>();
        var crossPlans=new List<(string Repository,List<long> Removed,List<CrossRepoEdge> Added)>();
        // LinkHttpRoutesAsync holds a single grouped source lease from before its
        // authoritative snapshot through this complete plan/apply interval.
        foreach (var repository in affected)
        {
            var binding=await IndexingPipeline.RebindHttpCallsAsync(_store,_logger,repository,new GraphBuffer(),
                includePersistedNodes:true,new HashSet<string>(),ct,preserveBindingsWithoutRawEvidence:true,
                work:work,sharedGlobalIndex:routeLookup,sharedRepositoryNames:repositoryNames);
            if (binding.ReboundEdges.Count>0 || !binding.Retractions.IsEmpty) localPlans.Add((repository,binding));
        }
        foreach (var (repository,sourceIds) in rawCallers)
        {
            var old=new Dictionary<string,CrossRepoEdge>(StringComparer.Ordinal);
            foreach (var edge in await _store.FindCrossRepoEdgesAsync(repository,EdgeType.HTTP_CALLS))
            {
                work.Text(edge.SourceRepository,edge.TargetRepository); work.Properties(edge.Properties);
                if (edge.SourceRepository.Equals(repository,StringComparison.OrdinalIgnoreCase) && sourceIds.Contains(edge.SourceNodeId)) old[HttpCrossEdgeKey(edge)]=edge;
            }
            var desired=nextCross.GetValueOrDefault(repository) ?? new(StringComparer.Ordinal);
            foreach (var edge in desired.Values) { work.Text(edge.SourceRepository,edge.TargetRepository); work.Properties(edge.Properties); }
            var stale=old.Where(pair=>!desired.TryGetValue(pair.Key,out var replacement) || HttpCrossProperties(pair.Value)!=HttpCrossProperties(replacement)).Select(pair=>pair.Value.Id).ToList();
            var added=desired.Where(pair=>!old.TryGetValue(pair.Key,out var existing) || HttpCrossProperties(existing)!=HttpCrossProperties(pair.Value)).Select(pair=>pair.Value).ToList();
            crossPlans.Add((repository,stale,added));
        }
        var localByOwner=localPlans.ToDictionary(plan=>plan.Repository,plan=>plan.Binding,StringComparer.OrdinalIgnoreCase);
        var crossByOwner=crossPlans.ToDictionary(plan=>plan.Repository,StringComparer.OrdinalIgnoreCase);
        var applyOwners=localByOwner.Keys.Concat(crossByOwner.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(Comparer<string>.Create((a,b)=> { work.Text(a,b); return StringComparer.Ordinal.Compare(a,b); })).ToArray();
        // Final refusal checkpoint. No budgeted matching, re-reading or serialization
        // follows it, so exhaustion cannot erase bindings. Cancellation is observed
        // between owners; each already-planned owner finishes its short commit pair.
        work.Charge();
        var completed=0;
        try
        {
            foreach (var repository in applyOwners)
            {
                ct.ThrowIfCancellationRequested();
                if (localByOwner.TryGetValue(repository,out var binding))
                {
                    await _store.ReplaceRepositoryFilesAsync(repository,[IndexingPipeline.HttpBindingSlice],[],
                        binding.ReboundEdges,new Dictionary<string,string>(),retractions:binding.Retractions,ct:CancellationToken.None);
                    _logger.LogInformation("HTTP target change in {TargetRepository} reconciled local bindings for {CallerRepository}: {Added} added, {Removed} removed",
                        scopeRepository ?? "fleet",repository,binding.ReboundEdges.Count,binding.Retractions.Edges.Count);
                }
                if (crossByOwner.TryGetValue(repository,out var cross))
                {
                    if (cross.Removed.Count>0) await _store.DeleteCrossRepoEdgesByIdsAsync(cross.Removed,CancellationToken.None);
                    if (cross.Added.Count>0) await _store.InsertCrossRepoEdgeBatchAsync(cross.Added,CancellationToken.None);
                }
                completed++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("HTTP binding apply cancelled after {Completed} of {Owners} planned owners; completed owners are committed, remaining owners retain prior bindings",completed,applyOwners.Length);
            throw;
        }
        return crossPlans.Sum(plan=>plan.Added.Count);
    }

    private static string HttpCrossEdgeKey(CrossRepoEdge edge) =>
        $"{edge.SourceRepository}\u001f{edge.TargetRepository}\u001f{edge.SourceNodeId}\u001f{edge.TargetNodeId}\u001f{edge.Type}";
    private static string HttpCrossProperties(CrossRepoEdge edge) =>
        JsonSerializer.Serialize(edge.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
}
