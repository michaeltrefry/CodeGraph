using CodeGraph.Data;
using CodeGraph.Extractors.TreeSitter;
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
/// sc-26200: file_hotspot answers C, C++, Go, Ruby, PHP and Bash files with churn and complexity.
/// A tree-sitter indexed repository whose files were each edited by a later bug-fix commit.
/// </summary>
public sealed class FileHotspotLanguagesTests : IDisposable
{
    private const string Repo = "PolyglotHotspots";

    private static readonly Dictionary<string, (string Source, int MaxDepth)> Files = new()
    {
        ["native/ring.c"] = ("""
            int ring_count(const int *items, int n) {
                int total = 0;
                for (int i = 0; i < n; i++) {
                    if (items[i] > 0) {
                        total++;
                    }
                }
                return total;
            }
            """, 3),
        ["native/ring.cpp"] = ("""
            namespace ring {
            int Count(const std::vector<int>& items) {
                int total = 0;
                for (int item : items) {
                    if (item > 0) {
                        total++;
                    }
                }
                return total;
            }
            }
            """, 4),
        ["cmd/ring/main.go"] = ("""
            package main

            func count(items []int) int {
                total := 0
                for _, item := range items {
                    if item > 0 {
                        total++
                    }
                }
                return total
            }
            """, 3),
        ["lib/ring.rb"] = ("""
            module Ring
              def self.count(items)
                total = 0
                items.each do |item|
                  if item > 0
                    total += 1
                  end
                end
                total
              end
            end
            """, 4),
        ["src/Ring.php"] = ("""
            <?php
            class Ring {
                public function count(array $items): int {
                    $total = 0;
                    foreach ($items as $item) {
                        if ($item > 0) {
                            $total++;
                        }
                    }
                    return $total;
                }
            }
            """, 4),
        ["scripts/ring.sh"] = ("""
            count() {
              local total=0
              for item in "$@"; do
                if [ "$item" -gt 0 ]; then
                  total=$((total + 1))
                fi
              done
              echo "$total"
            }
            """, 3)
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-hotspot-langs-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();
    private readonly string _headSha;

    public FileHotspotLanguagesTests()
    {
        Directory.CreateDirectory(_root);
        GitFixture.Run(_root, "init", "-q", "-b", "main");
        GitFixture.Run(_root, "config", "commit.gpgsign", "false");
        foreach (var (path, file) in Files)
            GitFixture.Write(_root, path, file.Source);
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "initial");
        foreach (var (path, file) in Files)
            GitFixture.Write(_root, path, file.Source + "\n");
        GitFixture.Run(_root, "commit", "-q", "-am", "fix bug in counting");
        _headSha = GitFixture.Run(_root, "rev-parse", "HEAD");

        new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexRepositoryAsync(Repo, _root, ct: CancellationToken.None).GetAwaiter().GetResult();
        _store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repo, LocalPath = _root, LastCommitSha = _headSha })
            .GetAwaiter().GetResult();
        _store.UpsertSyncStateAsync(new SyncStateEntity { Repository = Repo, LastCommitSha = _headSha })
            .GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("native/ring.c", "C")]
    [InlineData("native/ring.cpp", "C++")]
    [InlineData("cmd/ring/main.go", "Go")]
    [InlineData("lib/ring.rb", "Ruby")]
    [InlineData("src/Ring.php", "PHP")]
    [InlineData("scripts/ring.sh", "Bash")]
    public async Task FileHotspot_AnswersTheFile_WithChurnAndComplexity(string path, string language)
    {
        await Vitals().ComputeMetricsAsync(Repo, _root);

        var answer = (await Mcp().FileHotspot(Repo, path)).StructuredContent!.Value;

        answer.GetProperty("found").GetBoolean().ShouldBeTrue();
        answer.GetProperty("language").GetString().ShouldBe(language);
        answer.GetProperty("languageIndexed").GetBoolean().ShouldBeTrue();
        answer.GetProperty("churn365d").GetDouble().ShouldBeGreaterThan(0);
        answer.GetProperty("complexityScore").GetInt32().ShouldBeGreaterThan(0);
        answer.GetProperty("commitSha").GetString().ShouldBe(_headSha);
        var metric = (await _store.GetFileMetricAsync(Repo, path)).ShouldNotBeNull();
        metric.FunctionCount.ShouldBe(1);
        metric.MaxNestingDepth.ShouldBe(Files[path].MaxDepth);
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

    private VitalsAnalyzer Vitals() => new(
        _store,
        new NoAnalysisProviders(),
        Options.Create(new AnalysisOptions()),
        new LocalFileSystem(),
        new NoLint(),
        new LintResultCache(),
        new DiagnosticDetailCache(),
        NullLogger<VitalsAnalyzer>.Instance);

    private CodeGraphMcpServer Mcp()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var resolver = new CommitScopeResolver(
            _store,
            Options.Create(new RepositorySourceOptions()),
            new InMemoryBranchOverlayStore(),
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions()),
            time);
        return new CodeGraphMcpServer(
            new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
            null!,
            _store,
            null!,
            Options.Create(new RepositorySourceOptions()),
            null!,
            new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
            commitScopes: resolver,
            indexedLanguages: new IndexedLanguageCatalog([new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)]));
    }

    private sealed class NoLint : ILintRunner
    {
        public Task<IReadOnlyDictionary<string, LintResult>> LintRepositoryAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
    }

    private sealed class NoAnalysisProviders : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => throw new NotSupportedException();
    }
}
