using CodeGraph.Data;
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
/// an event App publishes.
/// </summary>
public sealed class ImpactCrossRepoDirectionTests
{
    private const string App = "App";
    private const string Lib = "Lib";
    private const string Client = "Client";
    private const string Sub = "Sub";

    private readonly TraversingStore _store = new();

    /// <summary>InMemoryGraphStore whose inbound traversal walks the intra-project edges.</summary>
    private sealed class TraversingStore : InMemoryGraphStore
    {
        public override async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
            TraceDirection direction, int maxDepth, EdgeType[]? edgeFilter = null, double minConfidence = 0)
        {
            var entries = new List<TraversalEntry>();
            var seen = new HashSet<long> { startNodeId };
            var frontier = new List<long> { startNodeId };
            for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<long>();
                foreach (var edge in await FindEdgesByTargetBatchAsync(frontier, edgeFilter))
                {
                    if (!seen.Add(edge.SourceId))
                        continue;
                    next.Add(edge.SourceId);
                    entries.Add(new TraversalEntry((await FindNodeByIdAsync(edge.SourceId))!, depth, edge.Type, edge.TargetId, null));
                }
                frontier = next;
            }
            return entries;
        }
    }

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
        // One row per dependent project: no TRANSITIVE duplicate for a direct dependent.
        report.CrossRepoImpacts
            .Select(c => (c.SourceProject, c.TargetProject, c.EdgeType, c.AffectedNodeCount))
            .OrderBy(c => c.SourceProject)
            .ShouldBe([(App, Sub, "PUBLISHES", 1), (Client, App, "CALLS", 1)]);
        report.Summary.CrossRepoCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProjectThatCallsAndConsumes_CountsAsOneCrossRepoProject()
    {
        var serve = Node(App, "serve", "src/api.rs");
        var fetch = Node(Client, "fetch", "src/main.rs");
        var onEvent = Node(Client, "on_ready", "src/events.rs");
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceProject = Client, TargetProject = App, SourceNodeId = fetch, TargetNodeId = serve, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = App, TargetProject = Client, SourceNodeId = serve, TargetNodeId = onEvent, Type = EdgeType.PUBLISHES }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);

        var report = (await impact.AnalyzeFileImpactAsync(App, "src/api.rs"))!;

        report.CrossRepoImpacts.Count.ShouldBe(2);
        report.Summary.CrossRepoCount.ShouldBe(1);
    }

    [Fact]
    public async Task ShorterPathFoundInALaterWave_LowersDepthAndReachesFurtherDependents()
    {
        // Core.parse is called by Plugin.entry and Rest.handle. Inside Plugin, wrap calls entry
        // and run calls wrap (depth 3), but run also calls Rest.handle (depth 2); Ext calls run.
        const string Core = "Core", Plugin = "Plugin", Rest = "Rest", Ext = "Ext";
        var parse = Node(Core, "parse", "src/parse.rs");
        var entry = Node(Plugin, "entry", "src/entry.rs");
        var wrap = Node(Plugin, "wrap", "src/wrap.rs");
        var run = Node(Plugin, "run", "src/run.rs");
        var handle = Node(Rest, "handle", "src/handle.rs");
        var ext = Node(Ext, "main", "src/main.rs");
        _store.AddEdge(new GraphEdge { Project = Plugin, SourceId = wrap, TargetId = entry, Type = EdgeType.CALLS });
        _store.AddEdge(new GraphEdge { Project = Plugin, SourceId = run, TargetId = wrap, Type = EdgeType.CALLS });
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceProject = Plugin, TargetProject = Core, SourceNodeId = entry, TargetNodeId = parse, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Rest, TargetProject = Core, SourceNodeId = handle, TargetNodeId = parse, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Plugin, TargetProject = Rest, SourceNodeId = run, TargetNodeId = handle, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Ext, TargetProject = Plugin, SourceNodeId = ext, TargetNodeId = run, Type = EdgeType.CALLS }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);

        var report = (await impact.AnalyzeFileImpactAsync(Core, "src/parse.rs"))!;

        report.AffectedNodes
            .Select(n => (n.Name, n.Depth))
            .OrderBy(n => n.Depth).ThenBy(n => n.Name)
            .ShouldBe([("entry", 1), ("handle", 1), ("run", 2), ("wrap", 2), ("main", 3)]);
    }

    [Fact]
    public async Task DependentsOfDependents_AreFollowedAcrossRepositories()
    {
        // Mid calls Core, Top calls Mid, and Core's own helper calls back into Mid.
        const string Core = "Core", Mid = "Mid", Top = "Top";
        var core = Node(Core, "parse", "src/parse.rs");
        var helper = Node(Core, "helper", "src/helper.rs");
        var mid = Node(Mid, "load", "src/load.rs");
        var top = Node(Top, "main", "src/main.rs");
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceProject = Mid, TargetProject = Core, SourceNodeId = mid, TargetNodeId = core, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Top, TargetProject = Mid, SourceNodeId = top, TargetNodeId = mid, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Core, TargetProject = Mid, SourceNodeId = helper, TargetNodeId = mid, Type = EdgeType.CALLS }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);

        var report = (await impact.AnalyzeFileImpactAsync(Core, "src/parse.rs"))!;

        report.AffectedNodes
            .Select(n => (n.Project, n.Name, n.Depth))
            .OrderBy(n => n.Depth).ThenBy(n => n.Project)
            .ShouldBe([(Mid, "load", 1), (Core, "helper", 2), (Top, "main", 2)]);
        // Mid→Core is fetched for both Core and Mid but counted once.
        report.CrossRepoImpacts
            .Select(c => (c.SourceProject, c.TargetProject, c.EdgeType, c.AffectedNodeCount))
            .OrderBy(c => c.SourceProject)
            .ShouldBe([(Core, Top, "TRANSITIVE", 1), (Mid, Core, "CALLS", 1)]);

        var oneHop = (await impact.AnalyzeFileImpactAsync(Core, "src/parse.rs", maxDepth: 1))!;
        oneHop.AffectedNodes.Select(n => n.Name).ShouldBe(["load"]);
    }
}
