using CodeGraph.Data;
using CodeGraph.Models;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// A small multi-repository graph exercising every cross-repo link kind. "Consumer" links to
/// "Provider" through Rust SCIP, Cargo, NuGet, HTTP and messaging; "Other" links to "Other2"
/// through Rust SCIP only, so a run scoped to Consumer must leave that pair alone.
/// </summary>
internal static class CrossRepoLinkFleet
{
    public const string Consumer = "Consumer";
    public const string Provider = "Provider";
    public const string Other = "Other";
    public const string Other2 = "Other2";

    private const string ProviderSymbol = "rust-analyzer cargo provider 1.0.0 provider/run().";
    private const string OtherSymbol = "rust-analyzer cargo other2 1.0.0 other2/serve().";
    private const string PackageKey = "cargo:registry:crates.io:provider-lib";

    public static IReadOnlyDictionary<string, (List<GraphNode> Nodes, List<PendingEdge> Edges)> Build()
    {
        var consumerNodes = new List<GraphNode>
        {
            Node(Consumer, NodeLabel.Function, "caller", "Consumer:caller", new() { ["source"] = "scip", ["scip_symbol"] = "rust-analyzer cargo consumer 1.0.0 consumer/caller()." }),
            Node(Consumer, NodeLabel.ExternalSymbol, "run", "Consumer:external:run", new()
            {
                ["source"] = "scip", ["scip_external"] = true, ["scip_symbol"] = ProviderSymbol,
                ["documentation"] = new string('d', 2000)
            }),
            Node(Consumer, NodeLabel.Package, "consumer", "package:consumer", new()
            {
                ["ecosystem"] = "cargo", ["is_definition"] = true, ["package_key"] = "cargo:registry:crates.io:consumer"
            }),
            Node(Consumer, NodeLabel.Package, "provider-lib", "package:provider-lib-ref", new()
            {
                ["ecosystem"] = "Cargo", ["is_definition"] = false, ["package_key"] = PackageKey,
                ["package_name"] = "provider-lib", ["local_name"] = "provider_alias", ["version"] = "1.5.0"
            }),
            Node(Consumer, NodeLabel.Package, "git-lib", "package:git-lib-ref", new()
            {
                ["ecosystem"] = "cargo", ["is_definition"] = false,
                ["package_key"] = "cargo:git:https://example.test/acme/provider-repo:git-lib",
                ["package_name"] = "git-lib", ["local_name"] = "git-lib", ["version"] = "",
                ["source_kind"] = "git", ["source"] = "https://example.test/Acme/Provider-Repo"
            }),
            Node(Consumer, NodeLabel.Package, "provider-repo", "package:swift-provider-ref", new()
            {
                ["ecosystem"] = "swiftpm", ["is_definition"] = false,
                ["package_key"] = "swiftpm:scm:example.test/acme/provider-repo",
                ["package_name"] = "provider-repo", ["identity"] = "provider-repo", ["version"] = "2.0.0",
                ["source_kind"] = "remote", ["source"] = "example.test/acme/provider-repo",
                ["location"] = "https://example.test/Acme/Provider-Repo.git", ["dependency_kind"] = "declared"
            }),
            Node(Consumer, NodeLabel.Repository, "Consumer", "Consumer"),
            Node(Consumer, NodeLabel.NuGetPackage, "Provider.Models", "nuget:Provider.Models", new() { ["version"] = "2.1.0" }),
            Node(Consumer, NodeLabel.Method, "GetItem", "Consumer.ItemClient.GetItem"),
            Node(Consumer, NodeLabel.Route, "GET api/items/{id}", "route:Provider:GET:api/items/{id}"),
            Node(Consumer, NodeLabel.Class, "ItemPublisher", "Consumer.ItemPublisher"),
            Node(Consumer, NodeLabel.Record, "ItemCreated", "Events.ItemCreated")
        };
        var consumerEdges = new List<PendingEdge>
        {
            new("Consumer:caller", "Consumer:external:run", EdgeType.CALLS, new() { ["line"] = 12 }),
            new("package:consumer", "package:provider-lib-ref", EdgeType.REFERENCES_PACKAGE),
            new("package:consumer", "package:git-lib-ref", EdgeType.REFERENCES_PACKAGE),
            new("Consumer", "nuget:Provider.Models", EdgeType.REFERENCES_PACKAGE),
            new("Consumer", "package:swift-provider-ref", EdgeType.REFERENCES_PACKAGE),
            new("Consumer.ItemClient.GetItem", "route:Provider:GET:api/items/{id}", EdgeType.HTTP_CALLS, new()
            {
                ["http_method"] = "GET", ["url_pattern"] = "api/items/5"
            }),
            new("Consumer.ItemPublisher", "Events.ItemCreated", EdgeType.PUBLISHES)
        };

        var providerNodes = new List<GraphNode>
        {
            Node(Provider, NodeLabel.Function, "run", "Provider:run", new()
            {
                ["source"] = "scip", ["scip_symbol"] = ProviderSymbol, ["documentation"] = new string('p', 2000)
            }),
            Node(Provider, NodeLabel.Package, "provider-lib", "package:provider-lib-definition", new()
            {
                ["ecosystem"] = "cargo", ["is_definition"] = "True", ["package_key"] = PackageKey, ["version"] = "1.5.0"
            }),
            Node(Provider, NodeLabel.Package, "git-lib", "package:git-lib-definition", new()
            {
                ["ecosystem"] = "cargo", ["is_definition"] = true,
                ["package_key"] = "cargo:workspace:provider:git-lib", ["version"] = "0.3.0"
            }),
            Node(Provider, NodeLabel.Package, "ProviderKit", "package:swift-provider-definition", new()
            {
                ["ecosystem"] = "swiftpm", ["is_definition"] = true,
                ["package_key"] = "swiftpm:local:provider:providerkit", ["version"] = "",
                ["manifest_path"] = "Package.swift"
            }),
            Node(Provider, NodeLabel.Route, "GET api/items/{id}", "Provider.ItemsController.Get", new()
            {
                ["http_method"] = "GET", ["route_template"] = "api/items/{id}"
            }),
            Node(Provider, NodeLabel.Class, "ItemCreatedConsumer", "Provider.ItemCreatedConsumer"),
            Node(Provider, NodeLabel.Record, "ItemCreated", "Events.ItemCreated")
        };
        var providerEdges = new List<PendingEdge>
        {
            new("Provider.ItemCreatedConsumer", "Events.ItemCreated", EdgeType.CONSUMES)
        };

        var otherNodes = new List<GraphNode>
        {
            Node(Other, NodeLabel.Function, "client", "Other:client"),
            Node(Other, NodeLabel.ExternalSymbol, "serve", "Other:external:serve", new()
            {
                ["source"] = "scip", ["scip_external"] = true, ["scip_symbol"] = OtherSymbol
            })
        };
        var otherEdges = new List<PendingEdge> { new("Other:client", "Other:external:serve", EdgeType.USES_TYPE) };

        var other2Nodes = new List<GraphNode>
        {
            Node(Other2, NodeLabel.Struct, "serve", "Other2:serve", new() { ["source"] = "scip", ["scip_symbol"] = OtherSymbol })
        };

        return new Dictionary<string, (List<GraphNode>, List<PendingEdge>)>
        {
            [Consumer] = (consumerNodes, consumerEdges),
            [Provider] = (providerNodes, providerEdges),
            [Other] = (otherNodes, otherEdges),
            [Other2] = (other2Nodes, [])
        };
    }

    /// <summary>Provider's remote, spelled differently from Consumer's git dependency on it.</summary>
    public static string? RepoUrl(string repository) =>
        repository == Provider ? "git@example.test:acme/provider-repo.git" : null;

    public static void SeedInMemory(InMemoryGraphStore store)
    {
        foreach (var (repository, (nodes, edges)) in Build())
        {
            store.AddRepository(new RepositoryInfo(repository, RepoUrl(repository), null, null, null, null, null, null, false, null));
            var ids = nodes.ToDictionary(node => node.QualifiedName, node => store.AddNode(node));
            foreach (var edge in edges)
            {
                store.AddEdge(new GraphEdge
                {
                    Repository = repository,
                    SourceId = ids[edge.SourceQN],
                    TargetId = ids[edge.TargetQN],
                    Type = edge.Type,
                    Properties = edge.Properties ?? new()
                });
            }
        }
    }

    /// <summary>The Consumer→Provider links, as (type, source QN, target QN) keyed for assertions.</summary>
    public static readonly (EdgeType Type, string SourceQN, string TargetQN)[] ConsumerLinks =
    [
        (EdgeType.HTTP_CALLS, "Consumer.ItemClient.GetItem", "Provider.ItemsController.Get"),
        (EdgeType.PUBLISHES, "Consumer.ItemPublisher", "Provider.ItemCreatedConsumer"),
        (EdgeType.REFERENCES_PACKAGE, "Consumer", "nuget:Provider.Models"),
        (EdgeType.REFERENCES_PACKAGE, "package:consumer", "package:provider-lib-definition"),
        (EdgeType.REFERENCES_PACKAGE, "package:consumer", "package:git-lib-definition"),
        (EdgeType.REFERENCES_PACKAGE, "Consumer", "package:swift-provider-definition"),
        (EdgeType.CALLS, "Consumer:caller", "Provider:run")
    ];

    public static readonly (EdgeType Type, string SourceQN, string TargetQN) OtherLink =
        (EdgeType.USES_TYPE, "Other:client", "Other2:serve");

    public static void ShouldMatch(
        IEnumerable<CrossRepoEdge> actual,
        Func<long, string> qualifiedNameOf,
        IEnumerable<(EdgeType Type, string SourceQN, string TargetQN)> expected)
    {
        actual.Select(edge => (edge.Type, qualifiedNameOf(edge.SourceNodeId), qualifiedNameOf(edge.TargetNodeId)))
            .OrderBy(link => link.ToString(), StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(link => link.ToString(), StringComparer.Ordinal));
    }

    private static GraphNode Node(
        string repository, NodeLabel label, string name, string qualifiedName,
        Dictionary<string, object>? properties = null) => new()
    {
        Repository = repository,
        Label = label,
        Name = name,
        QualifiedName = qualifiedName,
        FilePath = "src/lib.rs",
        Properties = properties ?? new()
    };
}
