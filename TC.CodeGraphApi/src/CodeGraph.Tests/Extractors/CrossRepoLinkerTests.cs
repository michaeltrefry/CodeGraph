using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Extractors;

public class CrossRepoLinkerTests
{
    private readonly InMemoryGraphStore _store = new();
    private readonly CrossRepoLinker _linker;

    public CrossRepoLinkerTests()
    {
        _linker = new CrossRepoLinker(_store,
            NullLogger<CrossRepoLinker>.Instance);
    }

    [Fact]
    public async Task Links_EventPublisher_ToConsumer_AcrossProjects()
    {
        _store.AddProject("ProjectA");
        _store.AddProject("ProjectB");

        // ProjectA publishes OrderCreatedEvent
        var publisherClass = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "ProjectA.OrderService"
        });

        var eventNode = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "TC.Orders.Models.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectA",
            SourceId = publisherClass,
            TargetId = eventNode,
            Type = EdgeType.PUBLISHES
        });

        // ProjectB consumes OrderCreatedEvent
        var consumerClass = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Class,
            Name = "OrderCreatedConsumer",
            QualifiedName = "ProjectB.OrderCreatedConsumer"
        });

        // Consumer's event node (same QN — shared via TC.*.Models)
        var eventNodeB = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "TC.Orders.Models.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectB",
            SourceId = consumerClass,
            TargetId = eventNodeB,
            Type = EdgeType.CONSUMES
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("ProjectA");
        crossEdge.TargetProject.ShouldBe("ProjectB");
        crossEdge.Type.ShouldBe(EdgeType.PUBLISHES);
        crossEdge.Properties["event_type"].ShouldBe("TC.Orders.Models.OrderCreatedEvent");
    }

    [Fact]
    public async Task Links_NuGetPackage_ToSourceProject()
    {
        _store.AddProject("TC.OrdersApi");
        _store.AddProject("TC.WalletApi");

        // TC.WalletApi references TC.OrdersApi.Models package
        var walletProject = _store.AddNode(new GraphNode
        {
            Project = "TC.WalletApi",
            Label = NodeLabel.Repository,
            Name = "TC.WalletApi",
            QualifiedName = "TC.WalletApi"
        });

        var nugetNode = _store.AddNode(new GraphNode
        {
            Project = "TC.WalletApi",
            Label = NodeLabel.NuGetPackage,
            Name = "TC.OrdersApi.Models",
            QualifiedName = "nuget:TC.OrdersApi.Models",
            Properties = new() { ["version"] = "1.2.3" }
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "TC.WalletApi",
            SourceId = walletProject,
            TargetId = nugetNode,
            Type = EdgeType.REFERENCES_PACKAGE,
            Properties = new() { ["version"] = "1.2.3" }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("TC.WalletApi");
        crossEdge.TargetProject.ShouldBe("TC.OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
        crossEdge.Properties["package_name"].ShouldBe("TC.OrdersApi.Models");
    }

    [Fact]
    public async Task Links_HttpCalls_ToRoutes_AcrossProjects()
    {
        _store.AddProject("ProjectA");
        _store.AddProject("ProjectB");

        // ProjectA has a route: GET /api/wallet/{id}
        var routeNode = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Route,
            Name = "GET api/wallet/{id}",
            QualifiedName = "route:ProjectA:GET:api/wallet/{id}",
            Properties = new()
            {
                ["http_method"] = "GET",
                ["route_template"] = "api/wallet/{id}"
            }
        });

        // ProjectB calls GET /api/wallet/123
        var callerMethod = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Method,
            Name = "GetWallet",
            QualifiedName = "ProjectB.OrderService.GetWallet"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectB",
            SourceId = callerMethod,
            TargetId = 0, // target is a URL pattern, not a resolved node
            Type = EdgeType.HTTP_CALLS,
            Properties = new()
            {
                ["http_method"] = "GET",
                ["url_pattern"] = "api/wallet/123"
            }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("ProjectB");
        crossEdge.TargetProject.ShouldBe("ProjectA");
        crossEdge.Type.ShouldBe(EdgeType.HTTP_CALLS);
    }

    [Fact]
    public async Task Skips_SameProject_Links()
    {
        _store.AddProject("ProjectA");

        // Publisher and consumer in same project
        var publisherClass = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "ProjectA.OrderService"
        });

        var eventNode = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "ProjectA.Models.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectA",
            SourceId = publisherClass,
            TargetId = eventNode,
            Type = EdgeType.PUBLISHES
        });

        var consumerClass = _store.AddNode(new GraphNode
        {
            Project = "ProjectA",
            Label = NodeLabel.Class,
            Name = "OrderCreatedConsumer",
            QualifiedName = "ProjectA.OrderCreatedConsumer"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectA",
            SourceId = consumerClass,
            TargetId = eventNode,
            Type = EdgeType.CONSUMES
        });

        await _linker.LinkAsync(CancellationToken.None);

        // Same project — no cross-repo edges
        _store.CrossEdges.Count.ShouldBe(0);
    }

    [Fact]
    public async Task NoOp_WhenNoData()
    {
        await _linker.LinkAsync(CancellationToken.None);
        _store.CrossEdges.Count.ShouldBe(0);
    }
}
