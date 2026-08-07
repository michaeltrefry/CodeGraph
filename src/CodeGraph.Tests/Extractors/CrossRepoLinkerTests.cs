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
            QualifiedName = "Orders.Messages.OrderCreatedEvent"
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

        // Consumer's event node (same QN shared across repos)
        var eventNodeB = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "Orders.Messages.OrderCreatedEvent"
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
        crossEdge.Properties["event_type"].ShouldBe("Orders.Messages.OrderCreatedEvent");
    }

    [Fact]
    public async Task Links_NuGetPackage_ToSourceProject()
    {
        _store.AddProject("OrdersApi");
        _store.AddProject("WalletApi");

        // WalletApi references OrdersApi.Models package
        var walletProject = _store.AddNode(new GraphNode
        {
            Project = "WalletApi",
            Label = NodeLabel.Repository,
            Name = "WalletApi",
            QualifiedName = "WalletApi"
        });

        var nugetNode = _store.AddNode(new GraphNode
        {
            Project = "WalletApi",
            Label = NodeLabel.NuGetPackage,
            Name = "OrdersApi.Models",
            QualifiedName = "nuget:OrdersApi.Models",
            Properties = new() { ["version"] = "1.2.3" }
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "WalletApi",
            SourceId = walletProject,
            TargetId = nugetNode,
            Type = EdgeType.REFERENCES_PACKAGE,
            Properties = new() { ["version"] = "1.2.3" }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("WalletApi");
        crossEdge.TargetProject.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
        crossEdge.Properties["package_name"].ShouldBe("OrdersApi.Models");
    }

    [Fact]
    public async Task Links_NonTc_NuGetPackage_ToSourceProject()
    {
        _store.AddProject("OrdersApi");
        _store.AddProject("WalletApi");

        var walletProject = _store.AddNode(new GraphNode
        {
            Project = "WalletApi",
            Label = NodeLabel.Repository,
            Name = "WalletApi",
            QualifiedName = "WalletApi"
        });

        var nugetNode = _store.AddNode(new GraphNode
        {
            Project = "WalletApi",
            Label = NodeLabel.NuGetPackage,
            Name = "OrdersApi.Contracts",
            QualifiedName = "nuget:OrdersApi.Contracts",
            Properties = new() { ["version"] = "2.0.0" }
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "WalletApi",
            SourceId = walletProject,
            TargetId = nugetNode,
            Type = EdgeType.REFERENCES_PACKAGE,
            Properties = new() { ["version"] = "2.0.0" }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("WalletApi");
        crossEdge.TargetProject.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
        crossEdge.Properties["package_name"].ShouldBe("OrdersApi.Contracts");
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
    public async Task Skips_Ambiguous_HttpRoute_Matches_AcrossProjects()
    {
        _store.AddProject("ProjectA");
        _store.AddProject("ProjectB");
        _store.AddProject("ProjectC");

        foreach (var project in new[] { "ProjectA", "ProjectC" })
        {
            _store.AddNode(new GraphNode
            {
                Project = project,
                Label = NodeLabel.Route,
                Name = "GET api/status",
                QualifiedName = $"route:{project}:GET:api/status",
                Properties = new()
                {
                    ["http_method"] = "GET",
                    ["route_template"] = "api/status"
                }
            });
        }

        var callerMethod = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Method,
            Name = "GetStatus",
            QualifiedName = "ProjectB.StatusClient.GetStatus"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectB",
            SourceId = callerMethod,
            TargetId = 0,
            Type = EdgeType.HTTP_CALLS,
            Properties = new()
            {
                ["http_method"] = "GET",
                ["url_pattern"] = "api/status"
            }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Uses_HttpCall_TargetRouteProjectHint_ToDisambiguate()
    {
        _store.AddProject("ProjectA");
        _store.AddProject("ProjectB");
        _store.AddProject("ProjectC");

        foreach (var project in new[] { "ProjectA", "ProjectC" })
        {
            _store.AddNode(new GraphNode
            {
                Project = project,
                Label = NodeLabel.Route,
                Name = "GET api/status",
                QualifiedName = $"route:{project}:GET:api/status",
                Properties = new()
                {
                    ["http_method"] = "GET",
                    ["route_template"] = "api/status"
                }
            });
        }

        var callerMethod = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Method,
            Name = "GetStatus",
            QualifiedName = "ProjectB.StatusClient.GetStatus"
        });

        var targetStub = _store.AddNode(new GraphNode
        {
            Project = "ProjectB",
            Label = NodeLabel.Route,
            Name = "GET api/status",
            QualifiedName = "route:ProjectA:GET:api/status"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "ProjectB",
            SourceId = callerMethod,
            TargetId = targetStub,
            Type = EdgeType.HTTP_CALLS,
            Properties = new()
            {
                ["http_method"] = "GET",
                ["url_pattern"] = "api/status"
            }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        _store.CrossEdges[0].SourceProject.ShouldBe("ProjectB");
        _store.CrossEdges[0].TargetProject.ShouldBe("ProjectA");
        _store.CrossEdges[0].Type.ShouldBe(EdgeType.HTTP_CALLS);
    }

    [Fact]
    public async Task Links_GatewayCalls_UsingExactServiceName_Only()
    {
        _store.AddProject("OrdersApi");
        _store.AddProject("GatewayApi");

        var ordersRoute = _store.AddNode(new GraphNode
        {
            Project = "OrdersApi",
            Label = NodeLabel.Route,
            Name = "GET api/orders/{id}",
            QualifiedName = "route:OrdersApi:GET:api/orders/{id}",
            Properties = new()
            {
                ["http_method"] = "GET",
                ["route_template"] = "api/orders/{id}"
            }
        });

        var gatewayCaller = _store.AddNode(new GraphNode
        {
            Project = "GatewayApi",
            Label = NodeLabel.Method,
            Name = "GetOrder",
            QualifiedName = "GatewayApi.Controllers.OrdersController.GetOrder"
        });

        _store.AddEdge(new GraphEdge
        {
            Project = "GatewayApi",
            SourceId = gatewayCaller,
            TargetId = ordersRoute,
            Type = EdgeType.HTTP_CALLS,
            Properties = new()
            {
                ["gateway_call"] = true,
                ["service_name"] = "OrdersApi",
                ["request_dto"] = "OrdersApi.Models.GetOrderRequest"
            }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceProject.ShouldBe("GatewayApi");
        crossEdge.TargetProject.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.HTTP_CALLS);
        crossEdge.Properties["request_dto"].ShouldBe("OrdersApi.Models.GetOrderRequest");
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
    public async Task Links_RustCalls_ByExactScipSymbolAcrossProjects()
    {
        const string symbol = "rust-analyzer cargo provider 1.2.3 provider/process().";
        var callerId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.Function,
            Name = "run",
            QualifiedName = "Consumer:run",
            Properties = new() { ["source"] = "scip" }
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.ExternalSymbol,
            Name = "process",
            QualifiedName = "Consumer:external:process",
            Properties = new()
            {
                ["source"] = "scip",
                ["scip_external"] = true,
                ["scip_symbol"] = symbol
            }
        });
        var definitionId = _store.AddNode(new GraphNode
        {
            Project = "Provider",
            Label = NodeLabel.Function,
            Name = "process",
            QualifiedName = "Provider:process",
            Properties = new()
            {
                ["source"] = "scip",
                ["scip_symbol"] = symbol
            }
        });
        _store.AddEdge(new GraphEdge
        {
            Project = "Consumer",
            SourceId = callerId,
            TargetId = externalId,
            Type = EdgeType.CALLS
        });

        await _linker.LinkAsync(CancellationToken.None);

        var edge = _store.CrossEdges.Single();
        edge.SourceProject.ShouldBe("Consumer");
        edge.TargetProject.ShouldBe("Provider");
        edge.SourceNodeId.ShouldBe(callerId);
        edge.TargetNodeId.ShouldBe(definitionId);
        edge.Type.ShouldBe(EdgeType.CALLS);
        edge.Properties["scip_symbol"].ShouldBe(symbol);
    }

    [Fact]
    public async Task Skips_RustSymbols_WhenExactSymbolIsAmbiguousAcrossProjects()
    {
        const string symbol = "rust-analyzer cargo shared 1.0.0 shared/run().";
        var callerId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.Function,
            Name = "caller",
            QualifiedName = "Consumer:caller"
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.ExternalSymbol,
            Name = "run",
            QualifiedName = "Consumer:external:run",
            Properties = new()
            {
                ["source"] = "scip",
                ["scip_external"] = true,
                ["scip_symbol"] = symbol
            }
        });
        foreach (var project in new[] { "ProviderA", "ProviderB" })
        {
            _store.AddNode(new GraphNode
            {
                Project = project,
                Label = NodeLabel.Function,
                Name = "run",
                QualifiedName = $"{project}:run",
                Properties = new()
                {
                    ["source"] = "scip",
                    ["scip_symbol"] = symbol
                }
            });
        }
        _store.AddEdge(new GraphEdge
        {
            Project = "Consumer",
            SourceId = callerId,
            TargetId = externalId,
            Type = EdgeType.CALLS
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task DoesNotLink_RustSymbols_ByDisplayNameAlone()
    {
        const string referencedSymbol = "rust-analyzer cargo expected 1.0.0 expected/run().";
        const string definitionSymbol = "rust-analyzer cargo other 1.0.0 other/run().";
        var callerId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.Function,
            Name = "caller",
            QualifiedName = "Consumer:caller"
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.ExternalSymbol,
            Name = "run",
            QualifiedName = "Consumer:external:run",
            Properties = new()
            {
                ["source"] = "scip",
                ["scip_external"] = true,
                ["scip_symbol"] = referencedSymbol
            }
        });
        _store.AddNode(new GraphNode
        {
            Project = "Provider",
            Label = NodeLabel.Function,
            Name = "run",
            QualifiedName = "Provider:run",
            Properties = new()
            {
                ["source"] = "scip",
                ["scip_symbol"] = definitionSymbol
            }
        });
        _store.AddEdge(new GraphEdge
        {
            Project = "Consumer",
            SourceId = callerId,
            TargetId = externalId,
            Type = EdgeType.CALLS
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task Links_CargoAliasDependency_ByCanonicalPackageIdentity()
    {
        const string packageKey = "cargo:registry:crates.io:provider-lib";
        var consumerId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.Package,
            Name = "consumer",
            QualifiedName = "package:consumer",
            Properties = new()
            {
                ["ecosystem"] = "cargo",
                ["is_definition"] = true,
                ["package_key"] = "cargo:registry:crates.io:consumer"
            }
        });
        var dependencyId = _store.AddNode(new GraphNode
        {
            Project = "Consumer",
            Label = NodeLabel.Package,
            Name = "provider-lib",
            QualifiedName = "package:provider-lib-ref",
            Properties = new()
            {
                ["ecosystem"] = "cargo",
                ["is_definition"] = false,
                ["package_key"] = packageKey,
                ["package_name"] = "provider-lib",
                ["local_name"] = "provider_alias",
                ["version"] = "1.5.0"
            }
        });
        var providerId = _store.AddNode(new GraphNode
        {
            Project = "Provider",
            Label = NodeLabel.Package,
            Name = "provider-lib",
            QualifiedName = "package:provider-lib-definition",
            Properties = new()
            {
                ["ecosystem"] = "cargo",
                ["is_definition"] = true,
                ["package_key"] = packageKey,
                ["version"] = "1.5.0"
            }
        });
        _store.AddEdge(new GraphEdge
        {
            Project = "Consumer",
            SourceId = consumerId,
            TargetId = dependencyId,
            Type = EdgeType.REFERENCES_PACKAGE
        });

        await _linker.LinkAsync(CancellationToken.None);

        var edge = _store.CrossEdges.Single();
        edge.SourceProject.ShouldBe("Consumer");
        edge.TargetProject.ShouldBe("Provider");
        edge.SourceNodeId.ShouldBe(consumerId);
        edge.TargetNodeId.ShouldBe(providerId);
        edge.Properties["local_name"].ShouldBe("provider_alias");
        edge.Properties["package_key"].ShouldBe(packageKey);
    }

    [Fact]
    public async Task NoOp_WhenNoData()
    {
        await _linker.LinkAsync(CancellationToken.None);
        _store.CrossEdges.Count.ShouldBe(0);
    }
}
