using CodeGraph.Models;
using CodeGraph.Tests.Extractors;
using Shouldly;

namespace CodeGraph.Tests.Data;

public sealed class CrossRepoEdgeDeletionTests
{
    [Fact]
    public async Task BoundedDeletionAndRepeatedUpsertPreserveOtherCrossRepositoryFacts()
    {
        var store = new InMemoryGraphStore();
        var edge = new CrossRepoEdge { SourceRepository = "A", TargetRepository = "B", SourceNodeId = 1, TargetNodeId = 2, Type = EdgeType.HTTP_CALLS, Properties = new() { ["owner"] = "http" } };
        await store.InsertCrossRepoEdgeBatchAsync([edge, edge with { Type = EdgeType.CALLS, Properties = new() { ["owner"] = "other" } }]);
        var selected = store.CrossEdges.Single(e => e.Type == EdgeType.HTTP_CALLS);
        var retained = store.CrossEdges.Single(e => e.Type == EdgeType.CALLS);
        selected.Id.ShouldBeGreaterThan(0);
        retained.Id.ShouldNotBe(selected.Id);
        await store.InsertCrossRepoEdgeAsync(edge);
        store.CrossEdges.Count.ShouldBe(2);
        store.CrossEdges.Single(e => e.Type == EdgeType.HTTP_CALLS).Id.ShouldBe(selected.Id);
        await store.DeleteCrossRepoEdgesByIdsAsync([selected.Id, selected.Id, long.MaxValue]);
        store.CrossEdges.Single().ShouldBe(retained);
        await store.DeleteCrossRepoEdgesByIdsAsync([]);
        store.CrossEdges.Single().ShouldBe(retained);
    }
}
