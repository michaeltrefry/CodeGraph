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
    public async Task Links_EventPublisher_ToConsumer_AcrossRepositories()
    {
        _store.AddRepository("RepositoryA");
        _store.AddRepository("RepositoryB");

        // RepositoryA publishes OrderCreatedEvent
        var publisherClass = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "RepositoryA.OrderService"
        });

        var eventNode = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "Orders.Messages.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryA",
            SourceId = publisherClass,
            TargetId = eventNode,
            Type = EdgeType.PUBLISHES
        });

        // RepositoryB consumes OrderCreatedEvent
        var consumerClass = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Class,
            Name = "OrderCreatedConsumer",
            QualifiedName = "RepositoryB.OrderCreatedConsumer"
        });

        // Consumer's event node (same QN shared across repos)
        var eventNodeB = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "Orders.Messages.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryB",
            SourceId = consumerClass,
            TargetId = eventNodeB,
            Type = EdgeType.CONSUMES
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceRepository.ShouldBe("RepositoryA");
        crossEdge.TargetRepository.ShouldBe("RepositoryB");
        crossEdge.Type.ShouldBe(EdgeType.PUBLISHES);
        crossEdge.Properties["event_type"].ShouldBe("Orders.Messages.OrderCreatedEvent");
    }

    [Fact]
    public async Task Links_NuGetPackage_ToSourceRepository()
    {
        _store.AddRepository("OrdersApi");
        _store.AddRepository("WalletApi");

        // WalletApi references OrdersApi.Models package
        var walletRepository = _store.AddNode(new GraphNode
        {
            Repository = "WalletApi",
            Label = NodeLabel.Repository,
            Name = "WalletApi",
            QualifiedName = "WalletApi"
        });

        var nugetNode = _store.AddNode(new GraphNode
        {
            Repository = "WalletApi",
            Label = NodeLabel.NuGetPackage,
            Name = "OrdersApi.Models",
            QualifiedName = "nuget:OrdersApi.Models",
            Properties = new() { ["version"] = "1.2.3" }
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "WalletApi",
            SourceId = walletRepository,
            TargetId = nugetNode,
            Type = EdgeType.REFERENCES_PACKAGE,
            Properties = new() { ["version"] = "1.2.3" }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceRepository.ShouldBe("WalletApi");
        crossEdge.TargetRepository.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
        crossEdge.Properties["package_name"].ShouldBe("OrdersApi.Models");
    }

    [Fact]
    public async Task Links_NonTc_NuGetPackage_ToSourceRepository()
    {
        _store.AddRepository("OrdersApi");
        _store.AddRepository("WalletApi");

        var walletRepository = _store.AddNode(new GraphNode
        {
            Repository = "WalletApi",
            Label = NodeLabel.Repository,
            Name = "WalletApi",
            QualifiedName = "WalletApi"
        });

        var nugetNode = _store.AddNode(new GraphNode
        {
            Repository = "WalletApi",
            Label = NodeLabel.NuGetPackage,
            Name = "OrdersApi.Contracts",
            QualifiedName = "nuget:OrdersApi.Contracts",
            Properties = new() { ["version"] = "2.0.0" }
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "WalletApi",
            SourceId = walletRepository,
            TargetId = nugetNode,
            Type = EdgeType.REFERENCES_PACKAGE,
            Properties = new() { ["version"] = "2.0.0" }
        });

        await _linker.LinkAsync(CancellationToken.None);

        _store.CrossEdges.Count.ShouldBe(1);
        var crossEdge = _store.CrossEdges[0];
        crossEdge.SourceRepository.ShouldBe("WalletApi");
        crossEdge.TargetRepository.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
        crossEdge.Properties["package_name"].ShouldBe("OrdersApi.Contracts");
    }

    [Fact]
    public async Task Links_HttpCalls_ToRoutes_AcrossRepositories()
    {
        _store.AddRepository("RepositoryA");
        _store.AddRepository("RepositoryB");

        // RepositoryA has a route: GET /api/wallet/{id}
        var routeNode = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Route,
            Name = "GET api/wallet/{id}",
            QualifiedName = "route:RepositoryA:GET:api/wallet/{id}",
            Properties = new()
            {
                ["http_method"] = "GET",
                ["route_template"] = "api/wallet/{id}"
            }
        });

        // RepositoryB calls GET /api/wallet/123
        var callerMethod = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Method,
            Name = "GetWallet",
            QualifiedName = "RepositoryB.OrderService.GetWallet"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryB",
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
        crossEdge.SourceRepository.ShouldBe("RepositoryB");
        crossEdge.TargetRepository.ShouldBe("RepositoryA");
        crossEdge.Type.ShouldBe(EdgeType.HTTP_CALLS);
    }

    [Fact]
    public async Task Skips_Ambiguous_HttpRoute_Matches_AcrossRepositories()
    {
        _store.AddRepository("RepositoryA");
        _store.AddRepository("RepositoryB");
        _store.AddRepository("RepositoryC");

        foreach (var repository in new[] { "RepositoryA", "RepositoryC" })
        {
            _store.AddNode(new GraphNode
            {
                Repository = repository,
                Label = NodeLabel.Route,
                Name = "GET api/status",
                QualifiedName = $"route:{repository}:GET:api/status",
                Properties = new()
                {
                    ["http_method"] = "GET",
                    ["route_template"] = "api/status"
                }
            });
        }

        var callerMethod = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Method,
            Name = "GetStatus",
            QualifiedName = "RepositoryB.StatusClient.GetStatus"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryB",
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
    public async Task Uses_HttpCall_TargetRouteRepositoryHint_ToDisambiguate()
    {
        _store.AddRepository("RepositoryA");
        _store.AddRepository("RepositoryB");
        _store.AddRepository("RepositoryC");

        foreach (var repository in new[] { "RepositoryA", "RepositoryC" })
        {
            _store.AddNode(new GraphNode
            {
                Repository = repository,
                Label = NodeLabel.Route,
                Name = "GET api/status",
                QualifiedName = $"route:{repository}:GET:api/status",
                Properties = new()
                {
                    ["http_method"] = "GET",
                    ["route_template"] = "api/status"
                }
            });
        }

        var callerMethod = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Method,
            Name = "GetStatus",
            QualifiedName = "RepositoryB.StatusClient.GetStatus"
        });

        var targetStub = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryB",
            Label = NodeLabel.Route,
            Name = "GET api/status",
            QualifiedName = "route:RepositoryA:GET:api/status"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryB",
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
        _store.CrossEdges[0].SourceRepository.ShouldBe("RepositoryB");
        _store.CrossEdges[0].TargetRepository.ShouldBe("RepositoryA");
        _store.CrossEdges[0].Type.ShouldBe(EdgeType.HTTP_CALLS);
    }

    [Fact]
    public async Task Links_GatewayCalls_UsingExactServiceName_Only()
    {
        _store.AddRepository("OrdersApi");
        _store.AddRepository("GatewayApi");

        var ordersRoute = _store.AddNode(new GraphNode
        {
            Repository = "OrdersApi",
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
            Repository = "GatewayApi",
            Label = NodeLabel.Method,
            Name = "GetOrder",
            QualifiedName = "GatewayApi.Controllers.OrdersController.GetOrder"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "GatewayApi",
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
        crossEdge.SourceRepository.ShouldBe("GatewayApi");
        crossEdge.TargetRepository.ShouldBe("OrdersApi");
        crossEdge.Type.ShouldBe(EdgeType.HTTP_CALLS);
        crossEdge.Properties["request_dto"].ShouldBe("OrdersApi.Models.GetOrderRequest");
    }

    [Fact]
    public async Task Skips_SameRepository_Links()
    {
        _store.AddRepository("RepositoryA");

        // Publisher and consumer in same repository
        var publisherClass = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "RepositoryA.OrderService"
        });

        var eventNode = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Record,
            Name = "OrderCreatedEvent",
            QualifiedName = "RepositoryA.Models.OrderCreatedEvent"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryA",
            SourceId = publisherClass,
            TargetId = eventNode,
            Type = EdgeType.PUBLISHES
        });

        var consumerClass = _store.AddNode(new GraphNode
        {
            Repository = "RepositoryA",
            Label = NodeLabel.Class,
            Name = "OrderCreatedConsumer",
            QualifiedName = "RepositoryA.OrderCreatedConsumer"
        });

        _store.AddEdge(new GraphEdge
        {
            Repository = "RepositoryA",
            SourceId = consumerClass,
            TargetId = eventNode,
            Type = EdgeType.CONSUMES
        });

        await _linker.LinkAsync(CancellationToken.None);

        // Same repository — no cross-repo edges
        _store.CrossEdges.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Links_RustCalls_ByExactScipSymbolAcrossProjects()
    {
        const string symbol = "rust-analyzer cargo provider 1.2.3 provider/process().";
        var callerId = _store.AddNode(new GraphNode
        {
            Repository = "Consumer",
            Label = NodeLabel.Function,
            Name = "run",
            QualifiedName = "Consumer:run",
            Properties = new() { ["source"] = "scip" }
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Repository = "Consumer",
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
            Repository = "Provider",
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
            Repository = "Consumer",
            SourceId = callerId,
            TargetId = externalId,
            Type = EdgeType.CALLS
        });

        await _linker.LinkAsync(CancellationToken.None);

        var edge = _store.CrossEdges.Single();
        edge.SourceRepository.ShouldBe("Consumer");
        edge.TargetRepository.ShouldBe("Provider");
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
            Repository = "Consumer",
            Label = NodeLabel.Function,
            Name = "caller",
            QualifiedName = "Consumer:caller"
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Repository = "Consumer",
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
        foreach (var repository in new[] { "ProviderA", "ProviderB" })
        {
            _store.AddNode(new GraphNode
            {
                Repository = repository,
                Label = NodeLabel.Function,
                Name = "run",
                QualifiedName = $"{repository}:run",
                Properties = new()
                {
                    ["source"] = "scip",
                    ["scip_symbol"] = symbol
                }
            });
        }
        _store.AddEdge(new GraphEdge
        {
            Repository = "Consumer",
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
            Repository = "Consumer",
            Label = NodeLabel.Function,
            Name = "caller",
            QualifiedName = "Consumer:caller"
        });
        var externalId = _store.AddNode(new GraphNode
        {
            Repository = "Consumer",
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
            Repository = "Provider",
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
            Repository = "Consumer",
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
            Repository = "Consumer",
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
            Repository = "Consumer",
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
            Repository = "Provider",
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
            Repository = "Consumer",
            SourceId = consumerId,
            TargetId = dependencyId,
            Type = EdgeType.REFERENCES_PACKAGE
        });

        await _linker.LinkAsync(CancellationToken.None);

        var edge = _store.CrossEdges.Single();
        edge.SourceRepository.ShouldBe("Consumer");
        edge.TargetRepository.ShouldBe("Provider");
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

    [Fact]
    public async Task LinkForRepositoryAsync_LinksScopeRepositoryWithoutLoadingWholeGraphRows()
    {
        var store = new FullScanRejectingGraphStore();
        CrossRepoLinkFleet.SeedInMemory(store);
        var linker = new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance);

        await linker.LinkForRepositoryAsync(CrossRepoLinkFleet.Consumer, CancellationToken.None);

        CrossRepoLinkFleet.ShouldMatch(store.CrossEdges, QualifiedNameOf(store), CrossRepoLinkFleet.ConsumerLinks);
        store.CrossEdges.ShouldAllBe(edge =>
            edge.SourceRepository == CrossRepoLinkFleet.Consumer && edge.TargetRepository == CrossRepoLinkFleet.Provider);
    }

    [Fact]
    public async Task LinkForRepositoryAsync_LeavesLinksBetweenOtherRepositoriesAlone()
    {
        CrossRepoLinkFleet.SeedInMemory(_store);

        await _linker.LinkForRepositoryAsync(CrossRepoLinkFleet.Consumer, CancellationToken.None);

        CrossRepoLinkFleet.ShouldMatch(_store.CrossEdges, QualifiedNameOf(_store), CrossRepoLinkFleet.ConsumerLinks);
    }

    [Fact]
    public async Task LinkAsync_LinksEveryRepositoryPair()
    {
        CrossRepoLinkFleet.SeedInMemory(_store);

        await _linker.LinkAsync(CancellationToken.None);

        CrossRepoLinkFleet.ShouldMatch(_store.CrossEdges, QualifiedNameOf(_store),
            [.. CrossRepoLinkFleet.ConsumerLinks, CrossRepoLinkFleet.OtherLink]);
    }

    private static Func<long, string> QualifiedNameOf(InMemoryGraphStore store) =>
        id => store.Nodes.Single(node => node.Id == id).QualifiedName;

    /// <summary>
    /// sc-25698: post-index linking loaded every CALLS edge and every definition node of every
    /// repository (with their JSON properties) and ran the indexer out of memory once one repository
    /// reached ~900k edges. The linker must read only the streamed link projections.
    /// </summary>
    private sealed class FullScanRejectingGraphStore : InMemoryGraphStore
    {
        public override Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000) =>
            throw new InvalidOperationException($"Linker loaded every {label} node row");

        public override Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type) =>
            throw new InvalidOperationException($"Linker loaded every {type} edge row");

        public override Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids) =>
            throw new InvalidOperationException($"Linker loaded {ids.Count} whole node rows");
    }
}
