using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class HttpTargetRebindingTests
{
    private readonly InMemoryGraphStore _store = new();
    private CrossRepoLinker Linker => new(_store, NullLogger<CrossRepoLinker>.Instance);

    private async Task SeedCaller(string repository, string? service = null, InMemoryGraphStore? destination = null)
    {
        var store = destination ?? _store;
        store.AddRepository(repository);
        var buffer = new GraphBuffer();
        buffer.AddNode(new() { Repository = repository, Label = NodeLabel.Function, Name = "fetchShared", QualifiedName = repository + ":fetchShared", FilePath = "ui/client.ts" });
        buffer.AddNode(new() { Repository = repository, Label = NodeLabel.Route, Name = "raw", QualifiedName = "route:*:GET:/shared", Properties = new() { ["stub"] = true } });
        buffer.AddNode(Endpoint(repository));
        var properties = new Dictionary<string, object> { ["http_method"] = "GET", ["url_pattern"] = "/shared", ["extraction_owner"] = "typescript" };
        if (service is not null) properties["service_name"] = service;
        buffer.AddEdge(new(repository + ":fetchShared", "route:*:GET:/shared", EdgeType.HTTP_CALLS, properties));
        var pipeline = new IndexingPipeline(store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
        await pipeline.RebindHttpCallsAsync(repository, buffer, false, new HashSet<string>(), CancellationToken.None);
        await store.ReplaceRepositoryFilesAsync(repository, ["ui/client.ts", "api/routes.go"], buffer.AllNodes.ToList(), buffer.AllPendingEdges.ToList(), new Dictionary<string, string> { ["ui/client.ts"] = "stable", ["api/routes.go"] = "stable" });
    }

    private static GraphNode Endpoint(string repository, string template = "/shared") => new()
    {
        Repository = repository, Label = NodeLabel.Route, Name = "GET " + template,
        QualifiedName = "route:" + repository + ":GET:" + template, FilePath = "api/routes.go",
        Properties = new() { ["http_method"] = "GET", ["route_template"] = template, ["route_kind"] = "endpoint" }
    };
    private IEnumerable<GraphEdge> Bound(string repository) => _store.Edges.Where(e => e.Repository == repository && e.Type == EdgeType.HTTP_CALLS && e.Properties.GetValueOrDefault("semantic_owner")?.ToString() == "http_binding");
    private string[] Facts(string repository) => _store.Edges.Where(e => e.Repository == repository && e.Type == EdgeType.HTTP_CALLS)
        .Select(e => $"{_store.Nodes.Single(n => n.Id == e.SourceId).QualifiedName}|{_store.Nodes.Single(n => n.Id == e.TargetId).QualifiedName}|{e.Properties.GetValueOrDefault("semantic_owner")}").Order().ToArray();

    [Fact]
    public async Task OtherRepositoryAddsAndRemovesEndpoint_ReconcilesPersistedLocalBindingWithoutReindexingCaller()
    {
        await SeedCaller("A");
        Bound("A").ShouldHaveSingleItem();
        var callerId = _store.Nodes.Single(n => n.QualifiedName == "A:fetchShared").Id;
        _store.AddRepository("B");
        await _store.UpsertNodeAsync(Endpoint("B"));
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        Bound("A").ShouldBeEmpty();
        _store.Edges.ShouldContain(e => e.SourceId == callerId && e.Properties.ContainsKey("extraction_owner"));
        var writes = _store.ReplaceRepositoryFilesCallCount;
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        await _store.DeleteNodesByFileAsync("B", "api/routes.go");
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        Bound("A").ShouldHaveSingleItem().SourceId.ShouldBe(callerId);
        Facts("A").ShouldBe(new[] { "A:fetchShared|route:*:GET:/shared|", "A:fetchShared|route:A:GET:/shared|http_binding" });
        writes = _store.ReplaceRepositoryFilesCallCount;
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }

    [Fact]
    public async Task ThirdRepositoryCollision_RetractsAndRestoresOtherPairWithoutChangingUnrelatedFacts()
    {
        await SeedCaller("A");
        await _store.DeleteNodesByFileAsync("A", "api/routes.go");
        _store.AddRepository("C");
        await _store.UpsertNodeAsync(Endpoint("C"));
        await Linker.LinkForRepositoryAsync("C", CancellationToken.None);
        var original = _store.CrossEdges.Single(edge => edge.Type == EdgeType.HTTP_CALLS);
        original.SourceRepository.ShouldBe("A");
        original.TargetRepository.ShouldBe("C");
        await _store.InsertCrossRepoEdgeAsync(original with { Id = 0, Type = EdgeType.CALLS, Properties = new() { ["untouched"] = true } });
        var unrelated = _store.CrossEdges.Single(edge => edge.Type == EdgeType.CALLS);
        var sourceId = _store.Nodes.Single(node => node.QualifiedName == "A:fetchShared").Id;
        var hashes = await _store.GetFileHashesAsync("A");
        _store.AddRepository("B");
        await _store.UpsertNodeAsync(Endpoint("B"));
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        _store.CrossEdges.ShouldNotContain(edge => edge.Type == EdgeType.HTTP_CALLS);
        _store.CrossEdges.Single().ShouldBe(unrelated);
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        _store.CrossEdges.Single().ShouldBe(unrelated);
        await _store.DeleteNodesByFileAsync("B", "api/routes.go");
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        var restored = _store.CrossEdges.Single(edge => edge.Type == EdgeType.HTTP_CALLS);
        restored.SourceNodeId.ShouldBe(sourceId);
        restored.TargetRepository.ShouldBe("C");
        _store.CrossEdges.Single(edge => edge.Type == EdgeType.CALLS).ShouldBe(unrelated);
        (await _store.GetFileHashesAsync("A")).ShouldBe(hashes);
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        _store.CrossEdges.Single(edge => edge.Type == EdgeType.HTTP_CALLS).Id.ShouldBe(restored.Id);
        var fresh = new InMemoryGraphStore();
        await SeedCaller("A", destination: fresh);
        await fresh.DeleteNodesByFileAsync("A", "api/routes.go");
        fresh.AddRepository("B"); fresh.AddRepository("C");
        await fresh.UpsertNodeAsync(Endpoint("C"));
        await new CrossRepoLinker(fresh, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
        HttpCrossFacts(_store).ShouldBe(HttpCrossFacts(fresh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitAmbiguousRawEvidenceRetractsPreviouslyDerivedRelation(bool crossRepository)
    {
        await SeedCaller("A");
        if (crossRepository)
        {
            await _store.DeleteNodesByFileAsync("A", "api/routes.go");
            _store.AddRepository("C");
            await _store.UpsertNodeAsync(Endpoint("C"));
            await Linker.LinkForRepositoryAsync("C", CancellationToken.None);
            _store.CrossEdges.ShouldHaveSingleItem();
        }
        else Bound("A").ShouldHaveSingleItem();
        var raw = _store.Edges.Single(edge => edge.Properties.ContainsKey("extraction_owner"));
        raw.Properties["binding_ambiguous"] = true;
        await Linker.LinkForRepositoryAsync("A", CancellationToken.None);
        Bound("A").ShouldBeEmpty();
        _store.CrossEdges.ShouldBeEmpty();
        _store.Edges.ShouldContain(edge => edge.Properties.ContainsKey("extraction_owner"));
    }

    [Fact]
    public async Task ConcreteExtractionOwnedLocalEdgeRemainsAuthoritative()
    {
        _store.AddRepository("A");
        var source = _store.AddNode(new() { Repository = "A", Label = NodeLabel.Function, Name = "client", QualifiedName = "A:client", FilePath = "ui/client.ts" });
        var target = _store.AddNode(Endpoint("A"));
        _store.AddEdge(new() { Repository = "A", SourceId = source, TargetId = target, Type = EdgeType.HTTP_CALLS,
            Properties = new() { ["http_method"] = "GET", ["url_pattern"] = "/shared", ["extraction_owner"] = "typescript" } });
        await Linker.LinkAsync(CancellationToken.None);
        _store.Edges.Single().Properties["extraction_owner"].ShouldBe("typescript");
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(0);
    }

    private static string[] HttpCrossFacts(InMemoryGraphStore store) => store.CrossEdges.Where(edge => edge.Type == EdgeType.HTTP_CALLS)
        .Select(edge => $"{edge.SourceRepository}|{store.Nodes.Single(node => node.Id == edge.SourceNodeId).QualifiedName}|{edge.TargetRepository}|{store.Nodes.Single(node => node.Id == edge.TargetNodeId).QualifiedName}")
        .Order().ToArray();

    [Fact]
    public async Task ExplicitServiceAndUnrelatedEndpointsDoNotChurnLocalBindingOrCallerGraph()
    {
        await SeedCaller("A", "A");
        var baseline = Facts("A");
        var writes = _store.ReplaceRepositoryFilesCallCount;
        _store.AddRepository("B");
        await _store.UpsertNodeAsync(Endpoint("B"));
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        Facts("A").ShouldBe(baseline);
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        await _store.UpsertNodeAsync(Endpoint("B", "/unrelated"));
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        Facts("A").ShouldBe(baseline);
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }

    [Fact]
    public async Task UnchangedPersistedEvidenceAfterIncompleteAnalysisIsPreservedAndGlobalLinkingAlsoReconciles()
    {
        await SeedCaller("A");
        _store.AddRepository("B");
        await _store.UpsertNodeAsync(Endpoint("B"));
        await Linker.LinkAsync(CancellationToken.None);
        Bound("A").ShouldBeEmpty();
        var baseline = Facts("A");
        var writes = _store.ReplaceRepositoryFilesCallCount;
        // An incomplete analyzer retains B's last successful endpoint slice. Linking must use
        // that persisted evidence rather than infer endpoint deletion from missing new output.
        await Linker.LinkForRepositoryAsync("B", CancellationToken.None);
        Facts("A").ShouldBe(baseline);
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }
}
