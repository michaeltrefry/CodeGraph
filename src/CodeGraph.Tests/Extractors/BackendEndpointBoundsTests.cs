using System.Collections;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public sealed class BackendEndpointBoundsTests
{
    private readonly TreeSitterExtractor _extractor = new(NullLogger<TreeSitterExtractor>.Instance);
    private static string Go(int routes, string handler = "handle") => "package main\nimport \"github.com/gin-gonic/gin\"\nfunc build(){ r:=gin.New();\n"
        + string.Join('\n', Enumerable.Range(0, routes).Select(i => $"r.GET(\"/route/{i}\", {handler})")) + "\n}";
    private static GraphNode Handler(string name, string file, int line = 1) => new()
    { Repository = "App", Label = NodeLabel.Function, Name = name, QualifiedName = file + "#" + name + line, FilePath = file, StartLine = line };
    private static ExtractorContext Context(BackendEndpointWorkBudget work) => new()
    { RepositoryName = "App", RootPath = "/test", BackendEndpointWork = work };

    [Fact]
    public async Task ScopedIndexIsReusedAcrossFiles_AndUnrelatedOrSameNamedOtherScopesDoNotMultiplyRouteWork()
    {
        var graph = new CountedNodes(Enumerable.Range(0, 100_000).Select(i => Handler("irrelevant" + i, "other/" + i + ".go"))
            .Concat(Enumerable.Range(0, 10_000).Select(i => Handler("handle", "other" + i + "/handler.go")))
            .Append(Handler("handle", "api/handler.go")).ToList());
        var work = new BackendEndpointWorkBudget();
        var index = new BackendEndpointHandlerIndex(graph, "App", work);
        var before = work.WorkUsed;
        var first = await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", Go(200), Context(work), index);
        first.Succeeded.ShouldBeTrue(first.FailureReason); first.Edges.Count.ShouldBe(200);
        var firstWork = work.WorkUsed - before;
        before = work.WorkUsed;
        var second = await _extractor.ExtractBackendEndpointsAsync("/test/api/more.go", Go(400), Context(work), index);
        second.Succeeded.ShouldBeTrue(second.FailureReason); second.Edges.Count.ShouldBe(400);
        (work.WorkUsed - before).ShouldBeLessThan(firstWork * 3);
        graph.Enumerations.ShouldBe(1);
        first.Edges.ShouldAllBe(edge => edge.TargetQN == "api/handler.go#handle1");
    }

    [Fact]
    public async Task DuplicateScopedCandidatesStayAmbiguous_AndLongSharedKeysConsumeWork()
    {
        var name = new string('h', 3000);
        var work = new BackendEndpointWorkBudget(2_000_000);
        var index = new BackendEndpointHandlerIndex([Handler(name, "api/one.go"), Handler(name, "api/two.go")], "App", work);
        var result = await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", Go(3, name), Context(work), index);
        result.Succeeded.ShouldBeTrue(result.FailureReason); result.Edges.ShouldBeEmpty();
        result.Nodes.ShouldAllBe(node => node.Properties["resolution_status"].Equals("ambiguous_handler"));
        var denied = new BackendEndpointWorkBudget(2_000_000);
        var repeated = await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", Go(100, name), Context(denied), index);
        repeated.Complete.ShouldBeFalse(); repeated.Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongSharedPathsAndScopesAreChargedBeforeRepeatedKeyOrOutputWork(bool rust)
    {
        var shared = new string('p', 3000);
        var work = new BackendEndpointWorkBudget(300_000);
        var path = rust ? "/test/src/lib.rs" : "/test/" + shared + "/routes.go";
        var source = rust ? "mod " + shared + " { use axum::{Router,routing::get}; fn handle(){} fn build(){let r=Router::new()"
            + string.Concat(Enumerable.Range(0, 100).Select(i => $".route(\"/{i}\",get(handle))")) + ";}}" : Go(100);
        var index = new BackendEndpointHandlerIndex([Handler("handle", rust ? "src/lib.rs" : shared + "/handler.go")], "App", work);
        var result = await _extractor.ExtractBackendEndpointsAsync(path, source, Context(work), index);
        result.Complete.ShouldBeFalse(); result.Succeeded.ShouldBeFalse(); result.FailureReason!.ShouldContain("budget");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortAliasesToLongImportedTargetsChargeEveryEmittedScope(bool rust)
    {
        var module = new string('m', 3000);
        string Source(int routes) => rust
            ? "use axum::{Router,routing::get};use crate::" + module + "::Handle as h;fn build(){let r=Router::new()"
                + string.Concat(Enumerable.Range(0, routes).Select(i => $".route(\"/{i}\",get(h))")) + ";}"
            : "package main\nimport (\"github.com/gin-gonic/gin\";h \"example.com/root/" + module
                + "\")\nfunc build(){r:=gin.New();\n" + string.Join('\n', Enumerable.Range(0, routes).Select(i => $"r.GET(\"/{i}\",h.Handle)")) + "}";
        ExtractorContext ImportedContext(BackendEndpointWorkBudget work) => new()
        {
            RepositoryName = "App", RootPath = "/test", BackendEndpointWork = work,
            GoModuleManifests = new Dictionary<string, string> { ["go.mod"] = "module example.com/root\n" }
        };
        var normal = new BackendEndpointWorkBudget(200_000);
        var index = new BackendEndpointHandlerIndex([Handler("Handle", rust ? "src/" + module + ".rs" : module + "/handler.go")
            with { QualifiedName = "App:actualHandle" }], "App", normal);
        var file = rust ? "/test/src/lib.rs" : "/test/routes.go";
        var accepted = await _extractor.ExtractBackendEndpointsAsync(file, Source(3), ImportedContext(normal), index);
        accepted.Complete.ShouldBeTrue(accepted.FailureReason); accepted.Edges.Count.ShouldBe(3);
        var limited = await _extractor.ExtractBackendEndpointsAsync(file, Source(100), ImportedContext(new(200_000)), index);
        limited.Complete.ShouldBeFalse(); limited.Succeeded.ShouldBeFalse(); limited.FailureReason!.ShouldContain("budget");
    }

    [Fact]
    public async Task BudgetIsSharedAcrossCalls_AndDepthAndRecursiveEvaluationRefuseIncompleteOutput()
    {
        var source = Go(5);
        var probe = new BackendEndpointWorkBudget();
        (await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", source, Context(probe), [])).Complete.ShouldBeTrue();
        var shared = new BackendEndpointWorkBudget(probe.WorkUsed + 1);
        (await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", source, Context(shared), [])).Complete.ShouldBeTrue();
        (await _extractor.ExtractBackendEndpointsAsync("/test/api/second.go", source, Context(shared), [])).Complete.ShouldBeFalse();
        var depth = await _extractor.ExtractBackendEndpointsAsync("/test/api/routes.go", source, Context(new(100_000, 3)), []);
        depth.Complete.ShouldBeFalse(); depth.FailureReason!.ShouldContain("depth");
        var chain = "use axum::{Router,routing::get}; fn handle(){} fn build(){let r=Router::new()"
            + string.Concat(Enumerable.Range(0, 650).Select(i => $".route(\"/{i}\",get(handle))")) + ";}";
        var recursive = await _extractor.ExtractBackendEndpointsAsync("/test/src/lib.rs", chain, Context(new()), []);
        recursive.Complete.ShouldBeFalse(); recursive.FailureReason!.ShouldContain("evaluation depth");
    }

    [Fact]
    public async Task CancellationDuringIndexConstructionStopsBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<GraphNode> Nodes()
        {
            for (var i = 0; i < 1000; i++)
            { if (i == 50) cancellation.Cancel(); yield return Handler("item" + i, "api/main.go"); }
        }
        var work = new BackendEndpointWorkBudget();
        await Should.ThrowAsync<OperationCanceledException>(() => _extractor.ExtractBackendEndpointsAsync(
            "/test/api/main.go", Go(1), Context(work), new CountedNodes(Nodes()), cancellation.Token));
    }

    [Fact]
    public async Task PipelineRefusalPreservesGraphAndHashes_AndSuccessfulIncrementalMatchesFreshWithoutChurn()
    {
        ExtractionVersionCatalog.PipelineVersion.ShouldBeGreaterThanOrEqualTo(11);
        var root = Path.Combine(Path.GetTempPath(), "endpoint-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "main.go"), "package main\nimport \"github.com/gin-gonic/gin\"\nfunc handle(){}\nfunc main(){r:=gin.New();r.GET(\"/one\",handle)}");
            var store = new InMemoryGraphStore();
            IndexingPipeline Pipeline(InMemoryGraphStore graph, long work = 100_000_000, int depth = 2048) => new(graph, [_extractor],
                Options.Create(new IndexingOptions { MaxBackendEndpointAnalysisWork = work, MaxBackendEndpointAnalysisDepth = depth }),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
            static string[] Facts(InMemoryGraphStore graph) => graph.Edges.Where(edge => edge.Type == EdgeType.HANDLES).Select(edge =>
                graph.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName + "→" + graph.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName).Order().ToArray();
            await Pipeline(store).IndexRepositoryAsync("App", root);
            var facts = Facts(store); var hashes = await store.GetFileHashesAsync("App"); var writes = store.ReplaceRepositoryFilesCallCount;
            await Pipeline(store).IndexRepositoryAsync("App", root); store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            foreach (var full in new[] { false, true })
            {
                await Should.ThrowAsync<InvalidOperationException>(() => Pipeline(store, work: 1).IndexRepositoryAsync("App", root, replaceExistingGraph: full));
                Facts(store).ShouldBe(facts); (await store.GetFileHashesAsync("App")).ShouldBe(hashes);
                await Should.ThrowAsync<InvalidOperationException>(() => Pipeline(store, depth: 2).IndexRepositoryAsync("App", root, replaceExistingGraph: full));
                Facts(store).ShouldBe(facts); (await store.GetFileHashesAsync("App")).ShouldBe(hashes);
            }
            File.WriteAllText(Path.Combine(root, "main.go"), File.ReadAllText(Path.Combine(root, "main.go")).Replace("/one", "/two"));
            await Pipeline(store).IndexRepositoryAsync("App", root);
            var fresh = new InMemoryGraphStore(); await Pipeline(fresh).IndexRepositoryAsync("App", root);
            Facts(store).ShouldBe(Facts(fresh));
            Pipeline(store, work: 1).ExtractionFingerprint.ShouldNotBe(Pipeline(store).ExtractionFingerprint);
            Pipeline(store, depth: 2).ExtractionFingerprint.ShouldNotBe(Pipeline(store).ExtractionFingerprint);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PipelineSharesBudgetAndIndexesAcrossPreliminaryAndResolvedPassesAndAllFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "endpoint-phase-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "one.go"), Go(1));
            File.WriteAllText(Path.Combine(root, "two.go"), Go(2));
            var probe = new PhaseProbe(_extractor);
            var pipeline = new IndexingPipeline(new InMemoryGraphStore(), [probe], Options.Create(new IndexingOptions()),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
            await pipeline.IndexRepositoryAsync("App", root);
            probe.Calls.Count.ShouldBe(4);
            probe.Calls.Select(call => call.Work).Distinct(ReferenceEqualityComparer.Instance).Count().ShouldBe(1);
            ReferenceEquals(probe.Calls[0].Index, probe.Calls[1].Index).ShouldBeTrue();
            ReferenceEquals(probe.Calls[2].Index, probe.Calls[3].Index).ShouldBeTrue();
            ReferenceEquals(probe.Calls[0].Index, probe.Calls[2].Index).ShouldBeFalse();
            probe.Calls[3].Used.ShouldBeGreaterThan(probe.Calls[0].Used);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class PhaseProbe(TreeSitterExtractor inner) : ICodeExtractor, IBackendEndpointExtractor
    {
        public List<(BackendEndpointWorkBudget Work, BackendEndpointHandlerIndex Index, long Used)> Calls { get; } = [];
        public IReadOnlySet<string> SupportedExtensions => inner.SupportedExtensions;
        public string GetExtractionVersion(string extension) => inner.GetExtractionVersion(extension);
        public bool SupportsBackendEndpoints(string extension) => inner.SupportsBackendEndpoints(extension);
        public bool MayContainBackendEndpoints(string extension, string content) => inner.MayContainBackendEndpoints(extension, content);
        public Task<ExtractionResult> ExtractAsync(string file, string content, ExtractorContext context, CancellationToken ct = default) => inner.ExtractAsync(file, content, context, ct);
        public Task<ExtractionResult> ExtractBackendEndpointsAsync(string file, string content, ExtractorContext context,
            IReadOnlyCollection<GraphNode> nodes, CancellationToken ct = default)
        {
            Calls.Add((context.BackendEndpointWork!, nodes.ShouldBeOfType<BackendEndpointHandlerIndex>(), context.BackendEndpointWork!.WorkUsed));
            return inner.ExtractBackendEndpointsAsync(file, content, context, nodes, ct);
        }
    }

    private sealed class CountedNodes(IEnumerable<GraphNode> nodes) : IReadOnlyCollection<GraphNode>
    {
        public int Enumerations;
        public int Count => throw new InvalidOperationException("Do not count/re-enumerate the candidate graph");
        public IEnumerator<GraphNode> GetEnumerator() { Enumerations++; return nodes.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
