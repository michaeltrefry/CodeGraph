using CodeGraph.Data;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26136: Swift on PR-head overlays and the per-file tools. A tree-sitter indexed Swift (and
/// Rust) repository: the default branch has Greeter.swift (called from Main.swift) and a Rust
/// crate; the PR head edits Greeter.swift and adds Farewell.swift, which only exists on the branch.
/// </summary>
public sealed class SwiftOverlayFileToolsTests : IDisposable
{
    private const string Repo = "SwiftOverlay";
    private const string GreeterPath = "Sources/App/Greeter.swift";
    private const string MainPath = "Sources/App/Main.swift";
    private const string FarewellPath = "Sources/App/Farewell.swift";
    private const string RustPath = "crates/core/src/lib.rs";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Greeter = """
        struct Greeter {
            func greet(names: [String]) -> String {
                var result = ""
                for name in names {
                    if name.isEmpty {
                        guard let first = name.first else {
                            continue
                        }
                        result += String(first)
                    }
                }
                return result
            }
        }

        """;

    private const string Rust = """
        pub fn classify(values: &[i32]) -> i32 {
            let mut total = 0;
            for value in values {
                match value {
                    0 => {
                        if total > 1 {
                            total += 1;
                        }
                    }
                    _ => total += value,
                }
            }
            total
        }

        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-overlay-swift-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();
    private readonly InMemoryBranchOverlayStore _overlays = new();
    private readonly MutableTimeProvider _time = new(Now);
    private readonly string _baseSha;
    private readonly string _headSha;

    public SwiftOverlayFileToolsTests()
    {
        Directory.CreateDirectory(_root);
        GitFixture.Run(_root, "init", "-q", "-b", "main");
        GitFixture.Run(_root, "config", "commit.gpgsign", "false");
        GitFixture.Write(_root, GreeterPath, Greeter.Replace("var result = \"\"", "var result = \"-\""));
        GitFixture.Write(_root, MainPath, "func run() {\n    let greeter = Greeter()\n    _ = greeter.greet(names: [\"a\"])\n}\n");
        GitFixture.Write(_root, RustPath, Rust.Replace("let mut total = 0;", "let mut total = 1;"));
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "initial");
        GitFixture.Write(_root, GreeterPath, Greeter);
        GitFixture.Write(_root, RustPath, Rust);
        GitFixture.Run(_root, "commit", "-q", "-am", "fix bug in greeting and classify");
        _baseSha = GitFixture.Run(_root, "rev-parse", "HEAD");

        GitFixture.Run(_root, "checkout", "-q", "-b", "topic");
        GitFixture.Write(_root, GreeterPath, "// head\n" + Greeter);
        GitFixture.Write(_root, FarewellPath,
            "func farewell() -> String {\n    let greeter = Greeter()\n    return greeter.greet(names: [\"bye\"])\n}\n");
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "head");
        _headSha = GitFixture.Run(_root, "rev-parse", "HEAD");
        GitFixture.Run(_root, "checkout", "-q", "main");

        Pipeline().IndexProjectAsync(Repo, _root, ct: CancellationToken.None).GetAwaiter().GetResult();
        _store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repo, LocalPath = _root, LastCommitSha = _baseSha })
            .GetAwaiter().GetResult();
        _store.UpsertSyncStateAsync(new SyncStateEntity { Project = Repo, LastCommitSha = _baseSha })
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SwiftPrHeadOverlay_BecomesReady_AndIndexesItsSwiftFiles()
    {
        var overlayId = await BuildOverlayAsync();

        var overlayFiles = (await _overlays.GetOverlayNodesAsync(overlayId))
            .Where(node => node.Label is not (NodeLabel.File or NodeLabel.Folder))
            .ToList();
        overlayFiles.Where(node => node.FilePath == FarewellPath).Select(node => node.Name).ShouldContain("farewell");
        overlayFiles.Where(node => node.FilePath == GreeterPath).Select(node => node.Name).ShouldContain("greet");

        // The overlay view answers the branch-only Swift symbol; the default branch does not have it.
        var head = (await Resolver().ResolveAsync(Repo, _headSha)).Graph;
        (await head.FindNodesByNameAsync(Repo, "farewell")).ShouldHaveSingleItem().FilePath.ShouldBe(FarewellPath);
        (await _store.FindNodesByNameAsync(Repo, "farewell")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnalyzeImpactFile_AnswersSwiftFiles_OnTheDefaultBranchAndAtAnOverlaySha()
    {
        var mcp = Mcp();

        var defaultBranch = (await mcp.AnalyzeImpactFile(Repo, GreeterPath)).StructuredContent!.Value;
        // (The in-memory store does not traverse, so only the overlay view's dependents are asserted.)
        defaultBranch.GetProperty("found").GetBoolean().ShouldBeTrue();

        await BuildOverlayAsync();

        var changed = await mcp.AnalyzeImpactFile(Repo, GreeterPath, sha: _headSha);
        changed.StructuredString("commitSha").ShouldBe(_headSha);
        var changedContent = changed.StructuredContent!.Value;
        changedContent.GetProperty("found").GetBoolean().ShouldBeTrue();
        // Both callers of greet: the unchanged Main.swift and the branch-only Farewell.swift.
        changedContent.GetProperty("totalAffected").GetInt32().ShouldBe(2);

        // Farewell.swift exists only on the branch: answered at the head sha, not on the default branch.
        var branchOnly = await mcp.AnalyzeImpactFile(Repo, FarewellPath, sha: _headSha);
        branchOnly.StructuredString("commitSha").ShouldBe(_headSha);
        branchOnly.StructuredContent!.Value.GetProperty("found").GetBoolean().ShouldBeTrue();
        (await mcp.AnalyzeImpactFile(Repo, FarewellPath)).StructuredContent!.Value
            .GetProperty("found").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(GreeterPath)]
    [InlineData(RustPath)]
    public async Task FileHotspot_AnswersSwiftAndRustFiles_WithChurnAndComplexity(string path)
    {
        await Vitals().ComputeMetricsAsync(Repo, _root);

        var answer = (await Mcp().FileHotspot(Repo, path)).StructuredContent!.Value;

        answer.GetProperty("found").GetBoolean().ShouldBeTrue();
        answer.GetProperty("churn365d").GetDouble().ShouldBeGreaterThan(0);
        answer.GetProperty("complexityScore").GetInt32().ShouldBeGreaterThan(0);
        answer.GetProperty("commitSha").GetString().ShouldBe(_baseSha);
        var metric = (await _store.GetFileMetricAsync(Repo, path)).ShouldNotBeNull();
        metric.FunctionCount.ShouldBe(1);
        metric.MaxNestingDepth.ShouldBeGreaterThanOrEqualTo(4);
        metric.BugFixCommits365d.ShouldBeGreaterThan(0);
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

    private IndexingPipeline Pipeline() => new(
        _store,
        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
        Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
        new LocalFileSystem(),
        NullLogger<IndexingPipeline>.Instance,
        swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    private async Task<long> BuildOverlayAsync()
    {
        var service = new BranchOverlayService(
            _overlays,
            _store,
            new FixedPathRepoProvider(_root),
            Pipeline(),
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions()),
            NullLogger<BranchOverlayService>.Instance,
            _time);

        var overlay = await service.CreateOverlayAsync("Michael", Repo, _headSha);
        await service.IndexOverlayAsync(overlay.Id);
        (await service.GetOverlayAsync(overlay.Id))!.Status.ShouldBe(BranchOverlayStatuses.Ready);
        return overlay.Id;
    }

    private VitalsAnalyzer Vitals() => new(
        _store,
        new NoAnalysisProviders(),
        Options.Create(new AnalysisOptions()),
        new LocalFileSystem(),
        new NoLint(),
        new LintResultCache(),
        new DiagnosticDetailCache(),
        NullLogger<VitalsAnalyzer>.Instance);

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

    private sealed class NoLint : ILintRunner
    {
        public Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
    }

    private sealed class NoAnalysisProviders : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => throw new NotSupportedException();
    }
}
