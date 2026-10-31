using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Cross-repo edges only make the depended-upon end's repository affected: a file that calls into
/// another repository's library has no dependents there, while callers and message consumers in other
/// repositories are dependents of the file. "App" calls into "Lib"; "Client" calls App; "Sub" consumes
/// an event App publishes.
/// </summary>
public sealed class ImpactCrossRepoDirectionTests
{
    private const string App = "App";
    private const string Lib = "Lib";
    private const string Client = "Client";
    private const string Sub = "Sub";

    private readonly TraversingStore _store = new();

    /// <summary>InMemoryGraphStore whose inbound traversal walks the intra-repository edges.</summary>
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

    private long Node(string repository, string name, string file) => _store.AddNode(new GraphNode
    {
        Repository = repository,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"{repository}:{file}:{name}",
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
            new CrossRepoEdge { SourceRepository = App, TargetRepository = Lib, SourceNodeId = adapter, TargetNodeId = catalog, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = App, TargetRepository = Lib, SourceNodeId = adapter, TargetNodeId = route, Type = EdgeType.HTTP_CALLS },
            new CrossRepoEdge { SourceRepository = App, TargetRepository = Lib, SourceNodeId = adapter, TargetNodeId = crate, Type = EdgeType.REFERENCES_PACKAGE },
            new CrossRepoEdge { SourceRepository = Client, TargetRepository = App, SourceNodeId = client, TargetNodeId = serve, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = App, TargetRepository = Sub, SourceNodeId = serve, TargetNodeId = handler, Type = EdgeType.PUBLISHES }
        ]);
        return new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
    }

    [Fact]
    public async Task NestedReturnedHookCallback_PropagatesToCallableOwnerAndComponentWithoutStructuralOwners()
    {
        var request = Node(App, "request", "src/api.ts");
        var callback = Node(App, "returnedCallback", "src/useData.ts");
        var hook = Node(App, "useData", "src/useData.ts");
        var screen = Node(App, "Screen", "src/screen.tsx");
        var file = _store.AddNode(new GraphNode { Repository = App, Label = NodeLabel.File,
            Name = "useData.ts", QualifiedName = $"{App}:src/useData.ts", FilePath = "src/useData.ts" });
        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Repository = App, SourceId = callback, TargetId = request, Type = EdgeType.CALLS },
            new GraphEdge { Repository = App, SourceId = hook, TargetId = callback, Type = EdgeType.DEFINES },
            new GraphEdge { Repository = App, SourceId = file, TargetId = callback, Type = EdgeType.DEFINES },
            new GraphEdge { Repository = App, SourceId = screen, TargetId = hook, Type = EdgeType.CALLS }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
        var report = (await impact.AnalyzeImpactAsync($"{App}:src/api.ts:request", App, 3))!;
        report.AffectedNodes.Select(node => (node.Name, node.Depth, node.EdgeType)).OrderBy(node => node.Depth)
            .ShouldBe([("returnedCallback", 1, "CALLS"), ("useData", 2, "DEFINES"), ("Screen", 3, "CALLS")]);
        report.AffectedNodes.ShouldNotContain(node => node.NodeId == file);
    }

    [Fact]
    public async Task CallsThroughComponentOwnedCallbacks_ReachRenderingScreens()
    {
        var helper = Node(App, "helper", "src/helper.ts");
        var callback = Node(App, "onClick", "src/widget.tsx");
        var widget = Node(App, "Widget", "src/widget.tsx");
        var screen = Node(App, "Screen", "src/screen.tsx");
        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Repository = App, SourceId = callback, TargetId = helper, Type = EdgeType.CALLS },
            new GraphEdge { Repository = App, SourceId = widget, TargetId = callback, Type = EdgeType.DEFINES_METHOD },
            new GraphEdge { Repository = App, SourceId = screen, TargetId = widget, Type = EdgeType.RENDERS }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
        var report = (await impact.AnalyzeImpactAsync($"{App}:src/helper.ts:helper", App, 3))!;
        report.AffectedNodes.Select(node => (node.Name, node.Depth, node.EdgeType)).OrderBy(node => node.Depth)
            .ShouldBe([("onClick", 1, "CALLS"), ("Widget", 2, "DEFINES_METHOD"), ("Screen", 3, "RENDERS")]);
    }

    [Theory]
    [InlineData(NodeLabel.File)]
    [InlineData(NodeLabel.Folder)]
    [InlineData(NodeLabel.DotnetProject)]
    [InlineData(NodeLabel.Repository)]
    [InlineData(NodeLabel.Namespace)]
    [InlineData(NodeLabel.Module)]
    [InlineData(NodeLabel.Package)]
    public async Task MethodDefinitionOwnership_DoesNotPromoteStructuralContainersOrTheirDependents(NodeLabel label)
    {
        var helper = Node(App, "helper", "src/helper.swift");
        var caller = Node(App, "run", "src/main.swift");
        var owner = _store.AddNode(new GraphNode { Repository = App, Label = label, Name = "container",
            QualifiedName = "container", FilePath = "src/main.swift" });
        var unrelated = Node(App, "unrelated", "src/other.swift");
        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Repository = App, SourceId = caller, TargetId = helper, Type = EdgeType.CALLS },
            new GraphEdge { Repository = App, SourceId = owner, TargetId = caller, Type = EdgeType.DEFINES_METHOD },
            new GraphEdge { Repository = App, SourceId = unrelated, TargetId = owner, Type = EdgeType.USES_TYPE }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
        var report = (await impact.AnalyzeImpactAsync($"{App}:src/helper.swift:helper", App, 5))!;
        report.AffectedNodes.Select(node => node.NodeId).ShouldBe([caller]);
    }

    [Fact]
    public async Task InterfaceAndImplementationMethodOwnership_StillReachesProvidersPagesAndRoutes()
    {
        long Add(string name, NodeLabel label) => _store.AddNode(new GraphNode { Repository = App, Label = label,
            Name = name, QualifiedName = name, FilePath = "src/app.ts" });
        var contract = Add("Repository", NodeLabel.Interface);
        var contractMethod = Add("Repository.load", NodeLabel.Method);
        var implementation = Add("Service", NodeLabel.Class);
        var implementationMethod = Add("Service.load", NodeLabel.Method);
        var page = Add("Page", NodeLabel.Component);
        var route = Add("/page", NodeLabel.Route);
        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Repository = App, SourceId = contract, TargetId = contractMethod, Type = EdgeType.DEFINES_METHOD },
            new GraphEdge { Repository = App, SourceId = implementation, TargetId = implementationMethod, Type = EdgeType.DEFINES_METHOD },
            new GraphEdge { Repository = App, SourceId = page, TargetId = contract, Type = EdgeType.INJECTS },
            new GraphEdge { Repository = App, SourceId = page, TargetId = implementation, Type = EdgeType.REGISTERS },
            new GraphEdge { Repository = App, SourceId = route, TargetId = page, Type = EdgeType.HANDLES }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);
        var contractReport = (await impact.AnalyzeImpactAsync("Repository.load", App, 3))!;
        contractReport.AffectedNodes.Select(node => (node.Name, node.Depth)).OrderBy(node => node.Depth)
            .ShouldBe([("Repository", 1), ("Page", 2), ("/page", 3)]);
        var implementationReport = (await impact.AnalyzeImpactAsync("Service.load", App, 3))!;
        implementationReport.AffectedNodes.Select(node => (node.Name, node.Depth)).OrderBy(node => node.Depth)
            .ShouldBe([("Service", 1), ("Page", 2), ("/page", 3)]);
    }

    [Fact]
    public async Task FileThatOnlyCallsAnotherRepository_HasNoCrossRepoDependents()
    {
        var impact = await SeedAsync();

        var report = (await impact.AnalyzeFileImpactAsync(App, "adapters/lib.rs"))!;

        report.AffectedNodes.ShouldBeEmpty();
        report.CrossRepoImpacts.ShouldBeEmpty();
        report.Summary.CriticalCount.ShouldBe(0);
        report.Summary.AffectedRepositories.ShouldBe([App]);
    }

    [Fact]
    public async Task ConsumerOfAnotherRepositoriesEvent_HasNoCrossRepoDependents()
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
            .Select(n => (n.Repository, n.Name, n.Depth, n.EdgeType, n.Risk))
            .OrderBy(n => n.Repository)
            .ShouldBe(
            [
                (Client, "fetch", 1, "CALLS", RiskLevel.Critical),
                (Sub, "on_ready", 1, "PUBLISHES", RiskLevel.Critical)
            ]);
        // One row per dependent repository: no TRANSITIVE duplicate for a direct dependent.
        report.CrossRepoImpacts
            .Select(c => (c.SourceRepository, c.TargetRepository, c.EdgeType, c.AffectedNodeCount))
            .OrderBy(c => c.SourceRepository)
            .ShouldBe([(App, Sub, "PUBLISHES", 1), (Client, App, "CALLS", 1)]);
        report.Summary.CrossRepoCount.ShouldBe(2);
    }

    [Fact]
    public async Task RepositoryThatCallsAndConsumes_CountsAsOneCrossRepoRepository()
    {
        var serve = Node(App, "serve", "src/api.rs");
        var fetch = Node(Client, "fetch", "src/main.rs");
        var onEvent = Node(Client, "on_ready", "src/events.rs");
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceRepository = Client, TargetRepository = App, SourceNodeId = fetch, TargetNodeId = serve, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = App, TargetRepository = Client, SourceNodeId = serve, TargetNodeId = onEvent, Type = EdgeType.PUBLISHES }
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
        _store.AddEdge(new GraphEdge { Repository = Plugin, SourceId = wrap, TargetId = entry, Type = EdgeType.CALLS });
        _store.AddEdge(new GraphEdge { Repository = Plugin, SourceId = run, TargetId = wrap, Type = EdgeType.CALLS });
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceRepository = Plugin, TargetRepository = Core, SourceNodeId = entry, TargetNodeId = parse, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = Rest, TargetRepository = Core, SourceNodeId = handle, TargetNodeId = parse, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = Plugin, TargetRepository = Rest, SourceNodeId = run, TargetNodeId = handle, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = Ext, TargetRepository = Plugin, SourceNodeId = ext, TargetNodeId = run, Type = EdgeType.CALLS }
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
            new CrossRepoEdge { SourceRepository = Mid, TargetRepository = Core, SourceNodeId = mid, TargetNodeId = core, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = Top, TargetRepository = Mid, SourceNodeId = top, TargetNodeId = mid, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceRepository = Core, TargetRepository = Mid, SourceNodeId = helper, TargetNodeId = mid, Type = EdgeType.CALLS }
        ]);
        var impact = new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance);

        var report = (await impact.AnalyzeFileImpactAsync(Core, "src/parse.rs"))!;

        report.AffectedNodes
            .Select(n => (n.Repository, n.Name, n.Depth))
            .OrderBy(n => n.Depth).ThenBy(n => n.Repository)
            .ShouldBe([(Mid, "load", 1), (Core, "helper", 2), (Top, "main", 2)]);
        // Mid→Core is fetched for both Core and Mid but counted once.
        report.CrossRepoImpacts
            .Select(c => (c.SourceRepository, c.TargetRepository, c.EdgeType, c.AffectedNodeCount))
            .OrderBy(c => c.SourceRepository)
            .ShouldBe([(Core, Top, "TRANSITIVE", 1), (Mid, Core, "CALLS", 1)]);

        var oneHop = (await impact.AnalyzeFileImpactAsync(Core, "src/parse.rs", maxDepth: 1))!;
        oneHop.AffectedNodes.Select(n => n.Name).ShouldBe(["load"]);
    }
}
