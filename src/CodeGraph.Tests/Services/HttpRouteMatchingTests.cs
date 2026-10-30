using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class HttpRouteMatchingTests
{
    private static LinkNodeRow Route(string template, string repo = "Api", string? qn = null,
        Dictionary<string, string?>? extra = null, string method = "GET") => new(1, repo, NodeLabel.Route, template,
        qn ?? $"route:{repo}:{method}:{template}", new Dictionary<string, string?>(extra ?? []) { ["http_method"] = method, ["route_template"] = template });
    private static List<LinkNodeRow> Match(string call, params LinkNodeRow[] routes) => CrossRepoLinker.SelectHttpRouteMatches("GET", call,
        null, null, "route:*:GET:" + call, null, routes.Select(route => route.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase),
        CrossRepoLinker.BuildHttpRouteLookup(routes));

    [Theory]
    [InlineData("/admin/v1/items/:id", "/admin/v1/items/{param}")]
    [InlineData("/admin/v1/items/{id:int}", "/admin/v1/items/123?include=all#details")]
    [InlineData("/admin/v1/items/{id}", "/admin/v1/items/slug")]
    [InlineData("/admin/v1/files/{*path}", "/admin/v1/files/a/b/c")]
    public void SharedNormalizer_MatchesParametersAndPreservesKnownPrefixes(string template, string call)
    {
        Match(call, Route(template)).ShouldHaveSingleItem();
        Match(call.Replace("/admin/v1", "/api/v1", StringComparison.Ordinal), Route(template)).ShouldBeEmpty();
    }

    [Fact]
    public void ConcreteStaticRouteWinsButEquivalentParameterizedIdentitiesStayAmbiguous()
    {
        var literal = Route("/api/items/status");
        Match("/api/items/status", literal, Route("/api/items/{id}")).ShouldBe([literal]);
        Match("/api/items/{param}", Route("/api/items/{id}"), Route("/api/items/:slug")).ShouldBeEmpty();
    }

    [Fact]
    public void EndpointPredicate_ExcludesPagesButRetainsLegacyAndExplicitNextApiRoutes()
    {
        var legacy = Route("/api/items");
        var nextApi = Route("/api/next", extra: new() { ["route_kind"] = "api", ["framework"] = "nextjs" });
        CrossRepoLinker.BuildHttpRouteLookup([
            legacy, nextApi,
            Route("/page", qn: "ui-route:Api:next:/page"),
            Route("/angular", extra: new() { ["component"] = "Page" }),
            Route("/react", extra: new() { ["route_kind"] = "ui" }),
            Route("/uncertain", extra: new() { ["registration_ambiguous"] = "true" })
        ]).Values.SelectMany(routes => routes).ShouldBe([legacy, nextApi]);
    }

    [Fact]
    public void ServiceHintIsAppliedBeforeSpecificityAndKnownOriginRequiresMatchingEvidence()
    {
        var localStatic = Route("/api/items/123", "UI");
        var remoteDynamic = Route("/api/items/{id}", "Api");
        var lookup = CrossRepoLinker.BuildHttpRouteLookup([localStatic, remoteDynamic]);
        Match("/api/items/123", Route("/api/items/{id}", method: "POST")).ShouldBeEmpty();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UI", "Api" };
        CrossRepoLinker.SelectHttpRouteMatches("GET", "/api/items/123", "Api", null, null, null, names, lookup)
            .ShouldBe([remoteDynamic]);
        CrossRepoLinker.SelectHttpRouteMatches("GET", "https://external.example/api/items/123", null, null, null,
            "https://external.example", names, lookup).ShouldBeEmpty();
        CrossRepoLinker.SelectHttpRouteMatches("GET", "https://Api/api/items/123", null, null, null,
            "https://Api", names, lookup).ShouldBe([remoteDynamic]);
        CrossRepoLinker.SelectHttpRouteMatches("GET", "/api/items/123", "UI", null, null,
            "https://Api", names, lookup).ShouldBeEmpty();
        CrossRepoLinker.SelectHttpRouteMatches("GET", "/api/items/123", "unknown", "UI.Models.GetRequest", null,
            null, names, lookup).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("UNKNOWN", "/api/items")]
    [InlineData("GET", "{param}")]
    [InlineData("GET", "tauri:load_items")]
    [InlineData("GET", "https://api.example/{param}")]
    public void UnknownMethodsOrNonHttpOperations_AreNotEligible(string method, string url) =>
        CrossRepoLinker.IsMatchableHttpCall(method, url).ShouldBeFalse();

    [Theory]
    [InlineData("route_kind", "ui")]
    [InlineData("component", "LegacyPage")]
    [InlineData("registration_ambiguous", true)]
    public async Task CrossRepoLinking_UsesTheSameEndpointExclusions(string key, object value)
    {
        var store = new InMemoryGraphStore();
        store.AddRepository("Client"); store.AddRepository("Api");
        store.AddNode(new GraphNode { Repository = "Api", Label = NodeLabel.Route, Name = "page", QualifiedName = "route:Api:GET:/api/items",
            Properties = new() { ["http_method"] = "GET", ["route_template"] = "/api/items", [key] = value } });
        var caller = store.AddNode(new GraphNode { Repository = "Client", Label = NodeLabel.Function, Name = "caller", QualifiedName = "caller" });
        store.AddEdge(new GraphEdge { Repository = "Client", SourceId = caller, TargetId = 0, Type = EdgeType.HTTP_CALLS,
            Properties = new() { ["http_method"] = "GET", ["url_pattern"] = "/api/items" } });
        await new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
        store.CrossEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossRepoLinking_DoesNotRebindLocalDerivedEdgesOrUnknownOperations()
    {
        var store = new InMemoryGraphStore();
        store.AddRepository("Client"); store.AddRepository("Api");
        store.AddNode(new GraphNode { Repository = "Api", Label = NodeLabel.Route, Name = "api", QualifiedName = "route:Api:GET:/api/items",
            Properties = new() { ["http_method"] = "GET", ["route_template"] = "/api/items" } });
        var caller = store.AddNode(new GraphNode { Repository = "Client", Label = NodeLabel.Function, Name = "caller", QualifiedName = "caller" });
        store.AddEdge(new GraphEdge { Repository = "Client", SourceId = caller, TargetId = 0, Type = EdgeType.HTTP_CALLS,
            Properties = new() { ["http_method"] = "GET", ["url_pattern"] = "/api/items", ["semantic_owner"] = "http_binding" } });
        store.AddEdge(new GraphEdge { Repository = "Client", SourceId = caller, TargetId = 0, Type = EdgeType.HTTP_CALLS,
            Properties = new() { ["http_method"] = "UNKNOWN", ["url_pattern"] = "/api/items" } });
        await new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
        store.CrossEdges.ShouldBeEmpty();
    }
}
