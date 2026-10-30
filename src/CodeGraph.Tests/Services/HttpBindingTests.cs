using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class HttpBindingTests
{
    private const string Repo = "Mono";
    private const string Caller = "typescript:Mono:apiFetch";
    private const string RawTarget = "route:*:GET:/admin/v1/items/{param}";
    private readonly InMemoryGraphStore _store = new();
    private IndexingPipeline Pipeline => new(_store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
    private static GraphNode Node(string qn, string path, NodeLabel label = NodeLabel.Function, Dictionary<string, object>? props = null) =>
        new() { Repository = Repo, Label = label, Name = qn, QualifiedName = qn, FilePath = path, Properties = props ?? [] };
    private static GraphNode Endpoint(string template = "/admin/v1/items/{id}", string? qn = null,
        Dictionary<string, object>? extra = null) => Node(qn ?? $"route:{Repo}:GET:{template}", "src/api.rs", NodeLabel.Route,
        new Dictionary<string, object>(extra ?? []) { ["http_method"] = "GET", ["route_template"] = template });
    private static PendingEdge Raw(Dictionary<string, object>? extra = null) => new(Caller, RawTarget, EdgeType.HTTP_CALLS,
        new Dictionary<string, object>(extra ?? []) { ["http_method"] = "GET", ["url_pattern"] = "/admin/v1/items/{param}", ["extraction_owner"] = "typescript" });
    private static HashSet<string> Paths(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);
    private static GraphBuffer InitialBuffer(Dictionary<string, object>? rawProperties = null)
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(Node(Caller, "ui/api.ts"));
        buffer.AddNode(Node("Screen", "ui/screen.tsx", NodeLabel.Component));
        buffer.AddNode(Node(RawTarget, "", NodeLabel.Route, new() { ["stub"] = true }));
        buffer.AddEdge(new PendingEdge("Screen", Caller, EdgeType.CALLS));
        buffer.AddEdge(Raw(rawProperties));
        AddEndpoint(buffer);
        return buffer;
    }
    private static void AddEndpoint(GraphBuffer buffer, string template = "/admin/v1/items/{id}", string handler = "get_items")
    {
        var endpoint = Endpoint(template);
        buffer.AddNode(endpoint);
        buffer.AddNode(Node(handler, "src/api.rs"));
        buffer.AddEdge(new PendingEdge(endpoint.QualifiedName, handler, EdgeType.HANDLES));
    }
    private Task<IndexingPipeline.HttpRebinding> Bind(GraphBuffer buffer, bool persisted, params string[] excluded) =>
        Pipeline.RebindHttpCallsAsync(Repo, buffer, persisted, Paths(excluded), CancellationToken.None);
    private async Task Persist(GraphBuffer buffer, IndexingPipeline.HttpRebinding binding, params string[] paths)
    {
        await _store.ReplaceRepositoryFilesAsync(Repo, paths,
            buffer.AllNodes.ToList(), buffer.AllPendingEdges.Concat(binding.ReboundEdges).DistinctBy(edge => (edge.SourceQN, edge.TargetQN, edge.Type)).ToList(),
            new Dictionary<string, string>(), retractions: binding.Retractions);
    }
    private List<GraphEdge> Bound() => _store.Edges.Where(edge => edge.Type == EdgeType.HTTP_CALLS
        && edge.Properties.GetValueOrDefault("semantic_owner")?.ToString() == "http_binding").ToList();
    private string[] Facts() => _store.Edges.Where(edge => edge.Type is EdgeType.HTTP_CALLS or EdgeType.HANDLES)
        .Select(edge => $"{_store.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName}|{_store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName}|{edge.Type}")
        .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task RawWildcardCallsRemainOwnedAndDistinctFromLocalEndpointBindings()
    {
        var buffer = InitialBuffer(new() { ["wrapped_http"] = true, ["origin_pattern"] = "{param}" });
        var binding = await Bind(buffer, false);
        binding.ReboundEdges.ShouldHaveSingleItem().Properties!["semantic_owner"].ShouldBe("http_binding");
        binding.ReboundEdges[0].Properties!.ShouldNotContainKey("extraction_owner");
        binding.ReboundEdges[0].Properties!["origin_unresolved"].ShouldBe(true);
        buffer.AllPendingEdges.ShouldContain(edge => edge.TargetQN == RawTarget && edge.Properties!["extraction_owner"].ToString() == "typescript");
        await Persist(buffer, binding, "ui/api.ts", "ui/screen.tsx", "src/api.rs");
        Bound().Count.ShouldBe(1);
        _store.Edges.Count(edge => edge.Type == EdgeType.HTTP_CALLS).ShouldBe(2);
        var impact = await new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance)
            .AnalyzeImpactAsync("get_items", Repo, 3);
        impact.ShouldNotBeNull().AffectedNodes.Select(node => node.QualifiedName)
            .ShouldContain(Caller);
        impact.AffectedNodes.ShouldContain(node => node.QualifiedName == "Screen");
    }

    [Fact]
    public async Task BackendOnlyRegistrationRetarget_UpdatesPersistedRawCallWithoutReplacingCaller()
    {
        var initial = InitialBuffer();
        await Persist(initial, await Bind(initial, false), "ui/api.ts", "ui/screen.tsx", "src/api.rs");
        var callerId = _store.Nodes.Single(node => node.QualifiedName == Caller).Id;
        var next = new GraphBuffer();
        AddEndpoint(next, "/admin/v1/items/:itemId", "get_items_v2");
        var binding = await Bind(next, true, "src/api.rs");
        binding.Retractions.Edges.ShouldHaveSingleItem();
        binding.ReboundEdges.ShouldHaveSingleItem().TargetQN.ShouldBe($"route:{Repo}:GET:/admin/v1/items/:itemId");
        await Persist(next, binding, "src/api.rs");
        _store.Nodes.Single(node => node.QualifiedName == Caller).Id.ShouldBe(callerId);
        Bound().ShouldHaveSingleItem().SourceId.ShouldBe(callerId);
        _store.Edges.ShouldContain(edge => edge.SourceId == callerId && _store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName == RawTarget);
        var fresh = InitialBuffer();
        fresh.RemoveEdges(edge => edge.Type == EdgeType.HANDLES);
        // Compare all HTTP/handler endpoint facts with a fresh graph for the revised registration.
        var freshStore = new InMemoryGraphStore();
        var freshBuffer = new GraphBuffer();
        foreach (var node in fresh.AllNodes.Where(node => node.FilePath != "src/api.rs")) freshBuffer.AddNode(node);
        foreach (var edge in fresh.AllPendingEdges.Where(edge => edge.Type != EdgeType.HTTP_CALLS || edge.TargetQN == RawTarget)) freshBuffer.AddEdge(edge);
        AddEndpoint(freshBuffer, "/admin/v1/items/:itemId", "get_items_v2");
        var freshPipeline = new IndexingPipeline(freshStore, [], Options.Create(new IndexingOptions()), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
        await freshPipeline.RebindHttpCallsAsync(Repo, freshBuffer, false, Paths(), CancellationToken.None);
        await freshStore.ReplaceRepositoryFilesAsync(Repo, ["ui/api.ts", "ui/screen.tsx", "src/api.rs"], freshBuffer.AllNodes.ToList(), freshBuffer.AllPendingEdges.ToList(), new Dictionary<string, string>());
        Facts().ShouldBe(freshStore.Edges.Where(edge => edge.Type is EdgeType.HTTP_CALLS or EdgeType.HANDLES)
            .Select(edge => $"{freshStore.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName}|{freshStore.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName}|{edge.Type}").Order(StringComparer.Ordinal).ToArray());
        var stable = await Bind(new GraphBuffer(), true);
        stable.ReboundEdges.ShouldBeEmpty();
        stable.Retractions.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task EndpointRemoval_RetractsOnlyDerivedBindingAndKeepsRawEvidence()
    {
        var buffer = InitialBuffer();
        await Persist(buffer, await Bind(buffer, false), "ui/api.ts", "src/api.rs");
        var removed = new GraphBuffer();
        var binding = await Bind(removed, true, "src/api.rs");
        binding.Retractions.Edges.ShouldHaveSingleItem();
        binding.ReboundEdges.ShouldBeEmpty();
        await Persist(removed, binding, "src/api.rs");
        Bound().ShouldBeEmpty();
        _store.Edges.ShouldContain(edge => edge.Type == EdgeType.HTTP_CALLS && edge.Properties.ContainsKey("extraction_owner"));
    }

    [Theory]
    [InlineData("service_name", "Other")]
    [InlineData("origin", "https://external.example")]
    [InlineData("service_name", "not-indexed")]
    public async Task KnownExternalOrUnresolvedServiceEvidence_DoesNotCreateLocalBinding(string key, string value)
    {
        _store.AddRepository("Other");
        var buffer = InitialBuffer(new() { [key] = value });
        (await Bind(buffer, false)).ReboundEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task IdenticalRemoteEndpointMakesPathOnlyLocalBindingAmbiguous()
    {
        _store.AddRepository("Other");
        await _store.UpsertNodeAsync(Endpoint() with { Repository = "Other", QualifiedName = "route:Other:GET:/admin/v1/items/:id" });
        (await Bind(InitialBuffer(), false)).ReboundEdges.ShouldBeEmpty();
        var explicitService = await Bind(InitialBuffer(new() { ["service_name"] = Repo }), false);
        explicitService.ReboundEdges.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("route_kind", "ui")]
    [InlineData("component", "LegacyAngularComponent")]
    [InlineData("framework", "angular")]
    [InlineData("registration_ambiguous", true)]
    public async Task FrontendAndAmbiguousRegistrationRoutes_AreNeverApiTargets(string key, object value)
    {
        var buffer = InitialBuffer();
        var route = Endpoint(extra: new() { [key] = value });
        buffer.AddNode(route);
        (await Bind(buffer, false)).ReboundEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConflictingOriginEvidenceCannotChooseAnArbitraryBinding()
    {
        var ambiguous = InitialBuffer(new() { ["binding_ambiguous"] = true });
        (await Bind(ambiguous, false)).ReboundEdges.ShouldBeEmpty();
        var duplicate = InitialBuffer(new() { ["service_name"] = Repo });
        duplicate.AddEdge(Raw(new() { ["service_name"] = "Other" }));
        (await Bind(duplicate, false)).ReboundEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task MeaningfulBindingPropertyChange_ReplacesPriorDerivedPropertiesAndThenStaysStable()
    {
        var initial = InitialBuffer(new() { ["transport"] = "fetch", ["wrapper_chain"] = "request" });
        await Persist(initial, await Bind(initial, false), "ui/api.ts", "src/api.rs");
        var changed = InitialBuffer(new() { ["transport"] = "fetch", ["wrapper_chain"] = "apiFetch->request" });
        var binding = await Bind(changed, true);
        binding.Retractions.Edges.ShouldHaveSingleItem();
        binding.ReboundEdges.ShouldHaveSingleItem();
        await Persist(changed, binding, IndexingPipeline.HttpBindingSlice);
        Bound().ShouldHaveSingleItem().Properties["wrapper_chain"].ShouldBe("apiFetch->request");
        var stable = await Bind(changed, true);
        stable.ReboundEdges.ShouldBeEmpty();
        stable.Retractions.IsEmpty.ShouldBeTrue();
    }
}
