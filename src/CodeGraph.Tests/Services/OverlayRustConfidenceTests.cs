using System.Text.Json;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Branch overlays over a semantically (SCIP) indexed Rust repository. The default-branch graph
/// carries SCIP qualified names; the overlay re-extracts the changed .rs file with tree-sitter.
/// Fixture: crates/alpha/src/lib.rs defines alpha_value and removed_fn, both called from the
/// unchanged crates/app/src/main.rs; the branch head edits alpha_value and deletes removed_fn.
/// </summary>
public sealed class OverlayRustConfidenceTests : IDisposable
{
    private const string Repo = "RustOverlay";
    private const string LibPath = "crates/alpha/src/lib.rs";
    private const string MainPath = "crates/app/src/main.rs";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-overlay-rust-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();
    private readonly InMemoryBranchOverlayStore _overlays = new();
    private readonly MutableTimeProvider _time = new(Now);
    private readonly string _baseSha;
    private readonly string _headSha;

    public OverlayRustConfidenceTests()
    {
        Directory.CreateDirectory(_root);
        GitFixture.Run(_root, "init", "-q", "-b", "main");
        GitFixture.Run(_root, "config", "commit.gpgsign", "false");
        GitFixture.Write(_root, LibPath,
            "pub fn alpha_value() -> i32 {\n    1\n}\n\npub fn removed_fn() -> i32 {\n    2\n}\n");
        GitFixture.Write(_root, MainPath,
            "fn main() {\n    alpha::alpha_value();\n    alpha::removed_fn();\n}\n");
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "base");
        _baseSha = GitFixture.Run(_root, "rev-parse", "HEAD");

        GitFixture.Run(_root, "checkout", "-q", "-b", "topic");
        GitFixture.Write(_root, LibPath,
            "// head\npub fn alpha_value() -> i32 {\n    3\n}\n\npub fn added_fn() -> i32 {\n    4\n}\n");
        GitFixture.Run(_root, "commit", "-q", "-am", "head");
        _headSha = GitFixture.Run(_root, "rev-parse", "HEAD");
        GitFixture.Run(_root, "checkout", "-q", "main");

        _store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repo, LocalPath = _root, LastCommitSha = _baseSha })
            .GetAwaiter().GetResult();
        _store.UpsertSyncStateAsync(new SyncStateEntity { Project = Repo, LastCommitSha = _baseSha })
            .GetAwaiter().GetResult();

        // A semantic default-branch index: SCIP qualified names, 0-based SCIP lines.
        var alpha = _store.AddNode(Scip("alpha_value", LibPath, 0));
        var removed = _store.AddNode(Scip("removed_fn", LibPath, 4));
        var main = _store.AddNode(Scip("main", MainPath, 0));
        _store.AddEdge(new GraphEdge { Project = Repo, SourceId = main, TargetId = alpha, Type = EdgeType.CALLS });
        _store.AddEdge(new GraphEdge { Project = Repo, SourceId = main, TargetId = removed, Type = EdgeType.CALLS });
    }

    [Fact]
    public async Task OverlayRustNodes_AreMarkedLowConfidenceTreeSitterFallback()
    {
        var overlayId = await BuildOverlayAsync();

        var code = (await _overlays.GetOverlayNodesAsync(overlayId))
            .Where(node => node.FilePath == LibPath && node.Label is not (NodeLabel.File or NodeLabel.Folder))
            .ToList();

        code.Select(node => node.Name).ShouldBe(["added_fn", "alpha_value"], ignoreOrder: true);
        code.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        code.ShouldAllBe(node => Property(node, GraphNodeConfidence.ExtractionSourceProperty) == GraphNodeConfidence.TreeSitterFallbackSource);
        code.ShouldAllBe(node => Property(node, GraphNodeConfidence.FallbackReasonProperty) == GraphNodeConfidence.OverlayFallbackReason);
    }

    [Fact]
    public async Task AnalyzeImpact_AtSha_KeepsCallerOfChangedRustSymbol_SaysLowConfidence_AndReportsUnmapped()
    {
        await BuildOverlayAsync();

        var mcp = await Mcp().AnalyzeImpact("alpha_value", Repo, sha: _headSha);

        mcp.StructuredString("commitSha").ShouldBe(_headSha);
        var text = mcp.Text();
        // main -> alpha_value survives: the SCIP-named base node maps to the tree-sitter overlay node.
        text.ShouldContain("**main** (Function)");
        text.ShouldContain("low-confidence node(s)");
        text.ShouldContain("[low confidence]");
        // main -> removed_fn has no branch counterpart.
        text.ShouldContain("**Unmapped callers:** 1 default-branch edge(s)");
        mcp.StructuredString("lowConfidence").ShouldBe("true");
        mcp.StructuredString("unmappedInboundEdges").ShouldBe("1");

        var rest = (ImpactReport)((OkObjectResult)(await ProjectsController().Impact(Repo, "alpha_value", sha: _headSha)).Result!).Value!;
        rest.AffectedNodes.Select(node => node.Name).ShouldContain("main");
        rest.Summary.LowConfidenceCount.ShouldBeGreaterThan(0);
        rest.UnmappedInboundEdges.ShouldBe(1);

        var defaultBranch = (ImpactReport)((OkObjectResult)(await ProjectsController().Impact(Repo, "alpha_value")).Result!).Value!;
        defaultBranch.UnmappedInboundEdges.ShouldBeNull();
    }

    [Fact]
    public async Task TraceCallPath_AtSha_ReturnsInferredCaller_AndSaysLowConfidence()
    {
        await BuildOverlayAsync();

        var trace = await Mcp().TraceCallPath("alpha_value", "inbound", project: Repo, sha: _headSha);

        var text = trace.Text();
        text.ShouldContain("**main** (Function)");
        text.ShouldContain("Start: **alpha_value** (Function) — RustOverlay [low confidence]");
        text.ShouldContain("low-confidence node(s)");
        text.ShouldContain("**Unmapped callers:** 1");
        trace.StructuredString("lowConfidenceCount").ShouldBe("1");
        trace.StructuredString("unmappedInboundEdges").ShouldBe("1");

        var graph = (await Resolver().ResolveAsync(Repo, _headSha)).Graph;
        var head = (await graph.FindNodesByNameAsync(Repo, "alpha_value")).ShouldHaveSingleItem();
        var inbound = (await graph.FindEdgesByTargetAsync(head.Id, EdgeType.CALLS)).ShouldHaveSingleItem();
        inbound.Properties[OverlayGraphView.InferredRetargetProperty].ShouldBe("name-match");

        var consolidated = await ConsolidatedMcpServer.GraphTrace(Mcp(), "call_path", "alpha_value", "inbound", project: Repo, sha: _headSha);
        using var envelope = JsonDocument.Parse(consolidated.Text());
        envelope.RootElement.GetProperty("lowConfidence").GetBoolean().ShouldBeTrue();
        envelope.RootElement.GetProperty("unmappedInboundEdges").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task TraceCallPath_AtSha_FromLowConfidenceNodeWithoutCallers_StillSaysLowConfidence()
    {
        await BuildOverlayAsync();

        var trace = await Mcp().TraceCallPath("added_fn", "inbound", project: Repo, sha: _headSha);

        trace.Body().ShouldStartWith("No call paths found for 'added_fn'.");
        trace.Text().ShouldContain("Start: **added_fn** (Function) — RustOverlay [low confidence]");
        trace.Text().ShouldContain("includes 1 low-confidence node(s)");
        trace.StructuredString("lowConfidence").ShouldBe("true");
    }

    [Fact]
    public async Task OverlayView_NameMatch_PicksClosestLine_AndLeavesTiesUnmapped()
    {
        var store = new InMemoryGraphStore();
        var caller = store.AddNode(Scip("caller", MainPath, 0));
        var near = store.AddNode(Scip("near", LibPath, 10));
        var tied = store.AddNode(Scip("tied", LibPath, 10));
        store.AddEdge(new GraphEdge { Project = Repo, SourceId = caller, TargetId = near, Type = EdgeType.CALLS });
        store.AddEdge(new GraphEdge { Project = Repo, SourceId = caller, TargetId = tied, Type = EdgeType.CALLS });

        var view = await OverlayGraphView.CreateAsync(
            store,
            new BranchOverlayEntity { Id = 1, Project = Repo, HeadSha = _headSha, BaseSha = _baseSha },
            [new BranchOverlayFileEntity { OverlayId = 1, FilePath = LibPath, ChangeKind = BranchOverlayChangeKinds.Modified }],
            [
                TreeSitter(11, "near", 12),
                TreeSitter(12, "near", 40),
                TreeSitter(13, "tied", 5),
                TreeSitter(14, "tied", 15)
            ],
            []);

        var targets = (await view.FindEdgesBySourceAsync(caller)).Select(edge => edge.TargetId).ToList();
        targets.ShouldBe([-11L]);
        view.UnmappedInboundEdgeCount.ShouldBe(1);
    }

    [Fact]
    public async Task SearchGraph_AtSha_TagsOverlayRustNodesLowConfidence()
    {
        await BuildOverlayAsync();

        var search = await Mcp().SearchGraph("alpha_value", project: Repo, sha: _headSha);

        search.Text().ShouldContain("**alpha_value** (Function) — RustOverlay [low confidence]");
        search.Text().ShouldContain("includes 1 low-confidence node(s)");
        search.StructuredString("lowConfidenceCount").ShouldBe("1");

        var rest = (NodeListResponse)((OkObjectResult)(await NodesController().Search("alpha_value", Repo, sha: _headSha)).Result!).Value!;
        rest.LowConfidenceCount.ShouldBe(1);
        rest.LowConfidence.ShouldBeTrue();
        var restDefault = (NodeListResponse)((OkObjectResult)(await NodesController().Search("alpha_value", Repo)).Result!).Value!;
        restDefault.LowConfidenceCount.ShouldBe(0);
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root))
                return;
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<long> BuildOverlayAsync()
    {
        var pipeline = new IndexingPipeline(
            _store,
            [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance);
        var service = new BranchOverlayService(
            _overlays,
            _store,
            new FixedPathRepoProvider(_root),
            pipeline,
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions()),
            NullLogger<BranchOverlayService>.Instance,
            _time);

        var overlay = await service.CreateOverlayAsync("Michael", Repo, _headSha);
        await service.IndexOverlayAsync(overlay.Id);
        (await service.GetOverlayAsync(overlay.Id))!.Status.ShouldBe(BranchOverlayStatuses.Ready);
        return overlay.Id;
    }

    private CommitScopeResolver Resolver() => new(
        _store,
        Options.Create(new RepositorySourceOptions()),
        _overlays,
        new BranchOverlayGit(),
        Options.Create(new BranchOverlayOptions()),
        _time);

    private CodeGraphMcpServer Mcp() => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
        commitScopes: Resolver());

    private ProjectsController ProjectsController() => new(
        null!,
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
        null!,
        Resolver());

    private NodesController NodesController() => new(
        new NodeQueryService(_store, Options.Create(new RepositorySourceOptions()), Resolver()),
        Resolver());

    private static string? Property(GraphNode node, string name)
        => node.Properties.TryGetValue(name, out var value)
            ? value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => value?.ToString()
            }
            : null;

    private static GraphNode Scip(string name, string path, int line) => new()
    {
        Project = Repo,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"{Repo}:scip:rust-analyzer cargo {(path == LibPath ? "alpha" : "app")} 0.1.0 {name}().",
        FilePath = path,
        StartLine = line,
        EndLine = line + 2,
        Properties = new Dictionary<string, object> { ["source"] = "scip" }
    };

    private static GraphNode TreeSitter(long id, string name, int line) => new()
    {
        Id = id,
        Project = Repo,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"{Repo}:{LibPath}#{name}@{line}",
        FilePath = LibPath,
        StartLine = line,
        EndLine = line + 1,
        Properties = new Dictionary<string, object>
        {
            [GraphNodeConfidence.ConfidenceProperty] = GraphNodeConfidence.Low,
            [GraphNodeConfidence.ExtractionSourceProperty] = GraphNodeConfidence.TreeSitterFallbackSource,
            [GraphNodeConfidence.FallbackReasonProperty] = GraphNodeConfidence.OverlayFallbackReason
        }
    };
}
