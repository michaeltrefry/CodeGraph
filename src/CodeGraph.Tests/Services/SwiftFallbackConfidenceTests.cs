using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Swift has no semantic extraction tier, so every node its tree-sitter extractor emits is a
/// tree-sitter fallback node, marked like Rust's fallback nodes (sc-26135, epic G2) — on the
/// default branch and in branch overlays — while other tree-sitter languages keep their marks.
/// </summary>
public sealed class SwiftFallbackConfidenceTests : IDisposable
{
    private const string Repo = "SwiftRepo";
    private const string ShapePath = "Sources/App/Shape.swift";
    private const string ToolPath = "scripts/tool.py";
    private const string ShapeSource = """
        protocol Drawable {
            func draw()
        }

        struct Shape: Drawable {
            var name: String { "shape" }
            func area() -> Double { 0 }
            func draw() {}
        }

        extension Shape {
            func describe() -> Double { area() }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-fallback-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();

    public SwiftFallbackConfidenceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Extractor_MarksEverySwiftNodeAsTreeSitterFallback_AndLeavesOtherLanguagesAlone()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var context = new ExtractorContext { RepositoryName = Repo, RootPath = _root };

        var swift = await extractor.ExtractAsync(Path.Combine(_root, ShapePath), ShapeSource, context);
        swift.Nodes.Count.ShouldBeGreaterThan(4);
        swift.Nodes.ShouldAllBe(node => IsSwiftFallback(node));

        var python = await extractor.ExtractAsync(Path.Combine(_root, ToolPath), "def helper():\n    return 1\n", context);
        python.Nodes.ShouldNotBeEmpty();
        python.Nodes.ShouldAllBe(node => !GraphNodeConfidence.IsLowConfidence(node)
                                         && !node.Properties.ContainsKey(GraphNodeConfidence.ExtractionSourceProperty));

        // Rust tree-sitter output is marked by the pipeline only when its semantic pass falls back.
        var rust = await extractor.ExtractAsync(Path.Combine(_root, "src/lib.rs"), "pub fn alpha() -> i32 { 1 }\n", context);
        rust.Nodes.ShouldNotBeEmpty();
        rust.Nodes.ShouldAllBe(node => !GraphNodeConfidence.IsLowConfidence(node));
    }

    [Fact]
    public void SwiftSpec_DeclaresNoSemanticTier_AndBumpsItsExtractionVersion()
    {
        LanguageSpecs.Swift.TreeSitterFallbackReason.ShouldBe(GraphNodeConfidence.SwiftSemanticUnavailableReason);
        LanguageSpecs.Swift.ExtractionVersion.ShouldBe(3);
        foreach (var other in new[] { LanguageSpecs.Rust, LanguageSpecs.Python })
            other.TreeSitterFallbackReason.ShouldBeNull();
    }

    [Fact]
    public async Task DefaultBranchIndex_StoresSwiftNodesAsFallback_AndAnswersSayLowConfidence()
    {
        Write(ShapePath, ShapeSource);
        Write(ToolPath, "def helper():\n    return 1\n");
        await Pipeline().IndexRepositoryAsync(Repo, _root, ct: CancellationToken.None);

        var swiftCode = Code(ShapePath);
        swiftCode.Select(node => node.Name).ShouldContain("area");
        swiftCode.ShouldAllBe(node => IsSwiftFallback(node));
        var pythonCode = Code(ToolPath);
        pythonCode.ShouldNotBeEmpty();
        pythonCode.ShouldAllBe(node => !GraphNodeConfidence.IsLowConfidence(node));

        var search = await Mcp().SearchGraph("area", repository: Repo);
        search.Text().ShouldContain("**area** (Method) — SwiftRepo [low confidence]");
        search.Text().ShouldContain("low-confidence node(s)");
        search.StructuredString("lowConfidence").ShouldBe("true");
        int.Parse(search.StructuredString("lowConfidenceCount")!).ShouldBeGreaterThan(0);

        var python = await Mcp().SearchGraph("helper", repository: Repo);
        python.StructuredString("lowConfidence").ShouldBe("false");

        var impact = await Mcp().AnalyzeImpact("area", Repo);
        impact.Text().ShouldContain("low-confidence node(s)");
        impact.StructuredString("lowConfidence").ShouldBe("true");
    }

    [Fact]
    public async Task BranchOverlay_MarksSwiftNodesAsFallback()
    {
        GitFixture.Run(_root, "init", "-q", "-b", "main");
        GitFixture.Run(_root, "config", "commit.gpgsign", "false");
        GitFixture.Write(_root, ShapePath, ShapeSource);
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "base");
        var baseSha = GitFixture.Run(_root, "rev-parse", "HEAD");
        GitFixture.Run(_root, "checkout", "-q", "-b", "topic");
        GitFixture.Write(_root, ShapePath, ShapeSource + "\nfunc added() {}\n");
        GitFixture.Run(_root, "commit", "-q", "-am", "head");
        var headSha = GitFixture.Run(_root, "rev-parse", "HEAD");
        GitFixture.Run(_root, "checkout", "-q", "main");
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repo, LocalPath = _root, LastCommitSha = baseSha });
        await _store.UpsertSyncStateAsync(new SyncStateEntity { Repository = Repo, LastCommitSha = baseSha });

        var overlays = new InMemoryBranchOverlayStore();
        var service = new BranchOverlayService(
            overlays, _store, new FixedPathRepoProvider(_root), Pipeline(), new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions()), NullLogger<BranchOverlayService>.Instance,
            new MutableTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
        var overlay = await service.CreateOverlayAsync("Michael", Repo, headSha);
        await service.IndexOverlayAsync(overlay.Id);
        (await service.GetOverlayAsync(overlay.Id))!.Status.ShouldBe(BranchOverlayStatuses.Ready);

        var code = (await overlays.GetOverlayNodesAsync(overlay.Id))
            .Where(node => node.FilePath == ShapePath && node.Label is not (NodeLabel.File or NodeLabel.Folder))
            .ToList();
        code.Select(node => node.Name).ShouldContain("added");
        code.ShouldAllBe(node => IsSwiftFallback(node));
    }

    private static bool IsSwiftFallback(GraphNode node) =>
        GraphNodeConfidence.IsLowConfidence(node)
        && Property(node, GraphNodeConfidence.ExtractionSourceProperty) == GraphNodeConfidence.TreeSitterFallbackSource
        && Property(node, GraphNodeConfidence.FallbackReasonProperty) == GraphNodeConfidence.SwiftSemanticUnavailableReason;

    private List<GraphNode> Code(string path) => _store.Nodes
        .Where(node => node.FilePath == path && node.Label is not (NodeLabel.File or NodeLabel.Folder))
        .ToList();

    private IndexingPipeline Pipeline() => new(
        _store,
        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
        Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
        new LocalFileSystem(),
        NullLogger<IndexingPipeline>.Instance);

    private CodeGraphMcpServer Mcp() => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance));

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string? Property(GraphNode node, string name)
        => node.Properties.TryGetValue(name, out var value)
            ? value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => value?.ToString()
            }
            : null;

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
}
