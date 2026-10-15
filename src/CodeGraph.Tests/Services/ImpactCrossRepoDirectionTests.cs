using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Cross-repo edges only make the depended-upon end's project affected: a file that calls into
/// another project's library has no dependents there, while callers and message consumers in other
/// projects are dependents of the file. "App" calls into "Lib"; "Client" calls App; "Sub" consumes
/// an event App publishes. InMemoryGraphStore.TraverseAsync does not walk intra-project edges, so
/// these pin the cross-repo hop only.
/// </summary>
public sealed class ImpactCrossRepoDirectionTests
{
    private const string App = "App";
    private const string Lib = "Lib";
    private const string Client = "Client";
    private const string Sub = "Sub";

    private readonly InMemoryGraphStore _store = new();

    private long Node(string project, string name, string file) => _store.AddNode(new GraphNode
    {
        Project = project,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"{project}:{file}:{name}",
        FilePath = file
    });

    private async Task<ImpactAnalysisService> SeedAsync()
    {
        // App's adapter calls Lib's catalog and route and references Lib's crate.
        var adapter = Node(App, "load", "adapters/lib.rs");
        var catalog = Node(Lib, "catalog", "src/catalog.rs");
        var route = Node(Lib, "get_catalog", "src/routes.rs");
        var crate = Node(Lib, "lib", "Cargo.toml");

        // App's api is called from Client and publishes an event Sub consumes.
        var serve = Node(App, "serve", "src/api.rs");
        var client = Node(Client, "fetch", "src/main.rs");
        var handler = Node(Sub, "on_ready", "src/handler.rs");

        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceProject = App, TargetProject = Lib, SourceNodeId = adapter, TargetNodeId = catalog, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = App, TargetProject = Lib, SourceNodeId = adapter, TargetNodeId = route, Type = EdgeType.HTTP_CALLS },
            new CrossRepoEdge { SourceProject = App, TargetProject = Lib, SourceNodeId = adapter, TargetNodeId = crate, Type = EdgeType.REFERENCES_PACKAGE },
            new CrossRepoEdge { SourceProject = Client, TargetProject = App, SourceNodeId = client, TargetNodeId = serve, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = App, TargetProject = Sub, SourceNodeId = serve, TargetNodeId = handler, Type = EdgeType.PUBLISHES }
        ]);
        return new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
    }

    [Fact]
    public async Task FileThatOnlyCallsAnotherProject_HasNoCrossRepoDependents()
    {
        var impact = await SeedAsync();

        var report = (await impact.AnalyzeFileImpactAsync(App, "adapters/lib.rs"))!;

        report.AffectedNodes.ShouldBeEmpty();
        report.CrossRepoImpacts.ShouldBeEmpty();
        report.Summary.CriticalCount.ShouldBe(0);
        report.Summary.AffectedProjects.ShouldBe([App]);
    }

    [Fact]
    public async Task ConsumerOfAnotherProjectsEvent_HasNoCrossRepoDependents()
    {
        var impact = await SeedAsync();

        var report = (await impact.AnalyzeFileImpactAsync(Sub, "src/handler.rs"))!;

        report.AffectedNodes.ShouldBeEmpty();
        report.CrossRepoImpacts.ShouldBeEmpty();
        report.Summary.CriticalCount.ShouldBe(0);
    }

    [Fact]
    public async Task CrossRepoCallersAndConsumers_StillRateCritical()
    {
        var impact = await SeedAsync();

        var report = (await impact.AnalyzeFileImpactAsync(App, "src/api.rs"))!;

        report.AffectedNodes
            .Select(n => (n.Project, n.Name, n.Depth, n.EdgeType, n.Risk))
            .OrderBy(n => n.Project)
            .ShouldBe(
            [
                (Client, "fetch", 1, "CALLS", RiskLevel.Critical),
                (Sub, "on_ready", 1, "PUBLISHES", RiskLevel.Critical)
            ]);
        report.CrossRepoImpacts
            .Where(c => c.EdgeType != "TRANSITIVE")
            .Select(c => (c.SourceProject, c.TargetProject, c.EdgeType))
            .OrderBy(c => c.SourceProject)
            .ShouldBe([(App, Sub, "PUBLISHES"), (Client, App, "CALLS")]);
    }
}
