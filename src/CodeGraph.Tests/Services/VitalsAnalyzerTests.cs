using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Tests.Extractors;
using System.Diagnostics;

namespace CodeGraph.Tests.Services;

public class VitalsAnalyzerTests
{
    [Fact]
    public async Task ComputeMetricsAsync_PersistsDiagnosticsAndKeepsRoslynLintCountsForCsharpFiles()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");

        try
        {
            Directory.CreateDirectory(Path.Combine(repoPath, "src"));
            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Foo.cs"), """
                namespace Example;

                public class Foo
                {
                    public void Bar()
                    {
                        if (true)
                        {
                            if (true)
                            {
                                if (true)
                                {
                                    System.Console.WriteLine("hi");
                                }
                            }
                        }
                    }
                }
                """);

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Foo.cs",
                QualifiedName = "src/Foo.cs",
                FilePath = "src/Foo.cs"
            });

            var lintCache = new LintResultCache();
            lintCache.Set("TestProject", new Dictionary<string, LintResult>
            {
                ["src/Foo.cs"] = new(1, 2)
            });

            var diagnosticCache = new DiagnosticDetailCache();
            diagnosticCache.Set("TestProject",
            [
                new ProjectDiagnosticEntity
                {
                    Project = "TestProject",
                    DotnetProject = "TestProject.Api",
                    Source = "roslyn",
                    DiagnosticKey = "TestProject.Api|CS8602|src/Foo.cs|12|12|Possible null dereference",
                    DiagnosticId = "CS8602",
                    Severity = "warning",
                    Message = "Possible null dereference",
                    Category = "Compiler",
                    FilePath = "src/Foo.cs",
                    LineStart = 12,
                    LineEnd = 12,
                    ComputedAt = DateTime.UtcNow
                }
            ]);

            var sidecarRunner = new StubLintRunner();
            var analyzer = CreateAnalyzer(store, lintCache, diagnosticCache, sidecarRunner);

            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            var metrics = await store.GetFileMetricsAsync("TestProject", "TestProject.Api");
            metrics.Count.ShouldBe(1);
            metrics[0].LintErrors.ShouldBe(1);
            metrics[0].LintWarnings.ShouldBe(2);
            // Not a git checkout: no commit is recorded.
            metrics[0].CommitSha.ShouldBeNull();

            var diagnostics = await store.GetProjectDiagnosticsAsync("TestProject", "TestProject.Api");
            diagnostics.Count.ShouldBe(1);
            diagnostics[0].DiagnosticId.ShouldBe("CS8602");
            diagnostics[0].FilePath.ShouldBe("src/Foo.cs");
            diagnostics[0].LineStart.ShouldBe(12);

            sidecarRunner.CallCount.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeMetricsAsync_ReplacesStoredDiagnosticsWhenNoNewDiagnosticsExist()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");

        try
        {
            Directory.CreateDirectory(Path.Combine(repoPath, "src"));
            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Foo.cs"), """
                namespace Example;

                public class Foo
                {
                    public void Bar()
                    {
                    }
                }
                """);

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Foo.cs",
                QualifiedName = "src/Foo.cs",
                FilePath = "src/Foo.cs"
            });

            await store.UpsertProjectDiagnosticsBatchAsync("TestProject",
            [
                new ProjectDiagnosticEntity
                {
                    Project = "TestProject",
                    DotnetProject = "TestProject.Api",
                    Source = "roslyn",
                    DiagnosticKey = "old",
                    DiagnosticId = "CS0001",
                    Severity = "warning",
                    Message = "Old diagnostic",
                    FilePath = "src/Foo.cs",
                    ComputedAt = DateTime.UtcNow
                }
            ]);

            var analyzer = CreateAnalyzer(store, new LintResultCache(), new DiagnosticDetailCache(), new StubLintRunner());

            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            var diagnostics = await store.GetProjectDiagnosticsAsync("TestProject");
            diagnostics.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeMetricsAsync_ComputesWeightedBugFixMetrics_AndConcernScoreBeyondRecentChurn()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");

        try
        {
            await InitializeGitRepoAsync(repoPath);

            Directory.CreateDirectory(Path.Combine(repoPath, "src"));
            var fooPath = Path.Combine(repoPath, "src", "Foo.cs");
            var barPath = Path.Combine(repoPath, "src", "Bar.cs");

            await File.WriteAllTextAsync(fooPath, "namespace Example; public class Foo { }");
            await File.WriteAllTextAsync(barPath, "namespace Example; public class Bar { }");
            await CommitAllAsync(repoPath, "initial commit", DateTime.UtcNow.AddDays(-220));

            await File.WriteAllTextAsync(fooPath, "namespace Example; public class Foo { public int Value => 1; }");
            await File.WriteAllTextAsync(barPath, "namespace Example; public class Bar { public int Value => 2; }");
            await CommitAllAsync(repoPath, "fix bug in services", DateTime.UtcNow.AddDays(-150));

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Foo.cs",
                QualifiedName = "src/Foo.cs",
                FilePath = "src/Foo.cs"
            });
            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Bar.cs",
                QualifiedName = "src/Bar.cs",
                FilePath = "src/Bar.cs"
            });

            var analyzer = CreateAnalyzer(store, new LintResultCache(), new DiagnosticDetailCache(), new StubLintRunner());

            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            var metrics = await store.GetFileMetricsAsync("TestProject", "TestProject.Api");
            metrics.Count.ShouldBe(2);

            var foo = metrics.Single(m => m.FilePath == "src/Foo.cs");
            foo.Changes.ShouldBe(0);
            foo.RiskScore.ShouldBe(0);
            foo.BugFixCommits365d.ShouldBe(0.5, 0.001);
            foo.BugFixRatio365d.ShouldBe(0.5, 0.001);
            foo.BugFixWeightedTouches365d.ShouldBe(1.0, 0.001);
            foo.ConcernScore.ShouldBeGreaterThan(0);
            // The checkout HEAD the metrics were computed at.
            var head = (await RunGitAsync(repoPath, "rev-parse HEAD")).Trim();
            metrics.ShouldAllBe(m => m.CommitSha == head);

            var repoSummary = (await store.GetProjectHealthSummariesAsync("TestProject"))
                .Single(s => string.IsNullOrEmpty(s.DotnetProject));
            repoSummary.HistoryMaturity.ShouldBe("Young");
            repoSummary.MonthlyCommitCounts.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeMetricsAsync_WeightsBroadFixCommitsByChangedLines()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");

        try
        {
            await InitializeGitRepoAsync(repoPath);

            Directory.CreateDirectory(Path.Combine(repoPath, "src"));
            var fooPath = Path.Combine(repoPath, "src", "Foo.cs");
            var barPath = Path.Combine(repoPath, "src", "Bar.cs");

            await File.WriteAllTextAsync(fooPath, "namespace Example;\npublic class Foo\n{\n    public void Run() { }\n}\n");
            await File.WriteAllTextAsync(barPath, "namespace Example;\npublic class Bar\n{\n    public void Run() { }\n}\n");
            await CommitAllAsync(repoPath, "initial commit", DateTime.UtcNow.AddDays(-220));

            await File.WriteAllTextAsync(fooPath, """
                namespace Example;
                public class Foo
                {
                    public void Run()
                    {
                        var value = 1;
                        value++;
                        value++;
                        value++;
                        value++;
                    }
                }
                """);
            await File.WriteAllTextAsync(barPath, """
                namespace Example;
                public class Bar
                {
                    public void Run()
                    {
                        var value = 1;
                    }
                }
                """);
            await CommitAllAsync(repoPath, "fix urgent production issue", DateTime.UtcNow.AddDays(-120));

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Foo.cs",
                QualifiedName = "src/Foo.cs",
                FilePath = "src/Foo.cs"
            });
            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Bar.cs",
                QualifiedName = "src/Bar.cs",
                FilePath = "src/Bar.cs"
            });

            var analyzer = CreateAnalyzer(store, new LintResultCache(), new DiagnosticDetailCache(), new StubLintRunner());

            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            var metrics = await store.GetFileMetricsAsync("TestProject", "TestProject.Api");
            var foo = metrics.Single(m => m.FilePath == "src/Foo.cs");
            var bar = metrics.Single(m => m.FilePath == "src/Bar.cs");

            foo.BugFixCommits365d.ShouldBeGreaterThan(bar.BugFixCommits365d);
            foo.BugFixWeightedTouches365d.ShouldBeGreaterThan(bar.BugFixWeightedTouches365d);
            foo.BugFixRatio365d.ShouldBeLessThanOrEqualTo(1.0);
            bar.BugFixRatio365d.ShouldBeLessThanOrEqualTo(1.0);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeMetricsAsync_RecordsNoCommit_WhenHeadMovesDuringTheRun()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");

        try
        {
            await InitializeGitRepoAsync(repoPath);
            Directory.CreateDirectory(Path.Combine(repoPath, "src"));
            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Foo.cs"), "namespace Example; public class Foo { }");
            await CommitAllAsync(repoPath, "initial commit", DateTime.UtcNow.AddDays(-10));

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                DotnetProject = "TestProject.Api",
                Label = NodeLabel.File,
                Name = "Foo.cs",
                QualifiedName = "src/Foo.cs",
                FilePath = "src/Foo.cs"
            });

            // Roslyn lint results make the analyzer run the lint step, during which the
            // checkout moves to a new commit (as a concurrent sync's fetch + reset would).
            var lintCache = new LintResultCache();
            lintCache.Set("TestProject", new Dictionary<string, LintResult> { ["src/Foo.cs"] = new(0, 1) });
            var movingRunner = new HeadMovingLintRunner(async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Foo.cs"),
                    "namespace Example; public class Foo { public int Value => 1; }");
                await CommitAllAsync(repoPath, "moved", DateTime.UtcNow.AddDays(-1));
            });
            var analyzer = CreateAnalyzer(store, lintCache, new DiagnosticDetailCache(), movingRunner);

            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            movingRunner.CallCount.ShouldBe(1);
            var metrics = await store.GetFileMetricsAsync("TestProject", "TestProject.Api");
            metrics.ShouldNotBeEmpty();
            metrics.ShouldAllBe(m => m.CommitSha == null);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    /// <summary>sc-26136: Swift and Rust are measured by braces, ignoring braces in comments and literals.</summary>
    [Theory]
    [InlineData("Sources/App/Shapes.swift", """
        /* { { { */
        protocol Shape {
            func area() -> Double
            func name() -> String
        }
        struct Box: Shape {
            func area() -> Double {
                let label = "{{{{"
                if true {
                    for _ in 0..<2 {
                        guard true else { return 0 }
                    }
                }
                return 1 // }}}
            }
            func name() -> String { "box" }
        }
        """, 5, 4, 9)]
    [InlineData("crates/core/src/lib.rs", """
        pub trait Shape {
            fn area(&self) -> f64;
        }
        impl Shape for Unit {
            fn area(&self) -> f64 {
                let open = '{';
                match self.kind {
                    Kind::A => {
                        if self.flag { 1.0 } else { 2.0 }
                    }
                    _ => 0.0,
                }
            }
        }
        fn helper<'a>(value: &'a str) -> &'a str { value }
        """, 5, 3, 9)]
    public void ComputeComplexityBatch_SwiftAndRust_CountBraceDepthAndFunctionBodies(
        string relPath, string source, int maxDepth, int functions, int longestFunction)
    {
        var repoPath = CreateTempRepo("TestProject");
        try
        {
            var fullPath = Path.Combine(repoPath, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);

            var analyzer = CreateAnalyzer(new InMemoryGraphStore(), new LintResultCache(), new DiagnosticDetailCache(), new StubLintRunner());
            var data = analyzer.ComputeComplexityBatch(repoPath, [relPath])[relPath];

            data.MaxNestingDepth.ShouldBe(maxDepth);
            data.FunctionCount.ShouldBe(functions);
            data.LongestFunction.ShouldBe(longestFunction);
            data.Score.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    /// <summary>
    /// sc-26136: Swift and Rust files get vitals, but not SwiftPM/Xcode dependency checkouts or a
    /// Cargo target/ directory's generated sources.
    /// </summary>
    [Fact]
    public async Task ComputeMetricsAsync_IncludesSwiftAndRust_ButNotDependencyCheckoutsOrCargoBuildOutput()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestProject");
        try
        {
            void Write(string relPath, string content)
            {
                var fullPath = Path.Combine(repoPath, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
            }

            Write("Sources/App/App.swift", "func run() {}\n");
            Write(".build/checkouts/Dep/Sources/Dep/Dep.swift", "func dep() {}\n");
            Write("DerivedData/App/SourcePackages/checkouts/Dep/Dep.swift", "func dep() {}\n");
            Write("crates/core/Cargo.toml", "[package]\nname = \"core\"\n");
            Write("crates/core/src/lib.rs", "pub fn core() {}\n");
            Write("crates/core/target/debug/build/core-1/out/generated.rs", "pub fn generated() {}\n");
            // A target/ directory that is not Cargo's build output is ordinary source.
            Write("tools/target/main.rs", "fn main() {}\n");

            store.AddNode(new GraphNode
            {
                Project = "TestProject",
                Label = NodeLabel.File,
                Name = "App.swift",
                QualifiedName = "Sources/App/App.swift",
                FilePath = "Sources/App/App.swift"
            });

            var analyzer = CreateAnalyzer(store, new LintResultCache(), new DiagnosticDetailCache(), new StubLintRunner());
            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            (await store.GetFileMetricsAsync("TestProject")).Select(metric => metric.FilePath)
                .ShouldBe(["Sources/App/App.swift", "crates/core/src/lib.rs", "tools/target/main.rs"], ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    private static VitalsAnalyzer CreateAnalyzer(
        InMemoryGraphStore store,
        LintResultCache lintCache,
        DiagnosticDetailCache diagnosticCache,
        ILintRunner sidecarRunner)
    {
        var compositeRunner = new CompositeLintRunner(
            lintCache,
            sidecarRunner,
            NullLogger<CompositeLintRunner>.Instance);

        return new VitalsAnalyzer(
            store,
            new StubAnalysisProviderRegistry(),
            Options.Create(new AnalysisOptions()),
            new LocalFileSystem(),
            compositeRunner,
            lintCache,
            diagnosticCache,
            NullLogger<VitalsAnalyzer>.Instance);
    }

    private static string CreateTempRepo(string repoName)
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-vitals-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, repoName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task InitializeGitRepoAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init");
        await RunGitAsync(repoPath, "config user.name \"CodeGraph Tests\"");
        await RunGitAsync(repoPath, "config user.email \"codegraph-tests@example.com\"");
    }

    private static async Task CommitAllAsync(string repoPath, string message, DateTime commitDateUtc)
    {
        await RunGitAsync(repoPath, "add .");
        var isoDate = commitDateUtc.ToString("O");
        await RunGitAsync(repoPath, $"commit -m \"{message}\"", ("GIT_AUTHOR_DATE", isoDate), ("GIT_COMMITTER_DATE", isoDate));
    }

    private static async Task<string> RunGitAsync(string repoPath, string arguments, params (string Name, string Value)[] environment)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var (name, value) in environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"git {arguments} failed.\nstdout: {stdout}\nstderr: {stderr}");
        return stdout;
    }

    private sealed class StubLintRunner : ILintRunner
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
        }
    }

    private sealed class HeadMovingLintRunner(Func<Task> moveHead) : ILintRunner
    {
        public int CallCount { get; private set; }

        public async Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default)
        {
            CallCount++;
            await moveHead();
            return new Dictionary<string, LintResult>();
        }
    }

    private sealed class StubAnalysisProviderRegistry : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => throw new NotSupportedException();
    }

    /// <summary>
    /// sc-25698: per-project health analyses ran their store reads and writes in parallel on the
    /// caller's scoped DbContext and failed with "A second operation was started on this context".
    /// </summary>
    [Fact]
    public async Task AnalyzeHealthAsync_DoesNotOverlapStoreOperations()
    {
        var store = new ConcurrencyDetectingGraphStore();
        string[] dotnetProjects = ["Demo.Api", "Demo.Core", "Demo.Jobs"];
        foreach (var dotnetProject in dotnetProjects.Append(null))
        {
            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = "Demo",
                DotnetProject = dotnetProject,
                OverallHealth = 6.0,
                TotalFiles = 1
            });
        }
        await store.UpsertFileMetricsBatchAsync("Demo", dotnetProjects
            .Select(dotnetProject => new FileMetricsEntity
            {
                Project = "Demo", DotnetProject = dotnetProject, FilePath = $"{dotnetProject}/A.cs"
            })
            .ToList());

        var analyzer = new VitalsAnalyzer(
            store,
            new SlowAnalysisProviderRegistry(),
            Options.Create(new AnalysisOptions()),
            new LocalFileSystem(),
            new CompositeLintRunner(new LintResultCache(), new NoOpLintRunner(), NullLogger<CompositeLintRunner>.Instance),
            new LintResultCache(),
            new DiagnosticDetailCache(),
            NullLogger<VitalsAnalyzer>.Instance);

        await analyzer.AnalyzeHealthAsync("Demo");

        store.OverlapDetected.ShouldBeFalse();
        (await store.GetProjectHealthAnalysesAsync("Demo")).Select(a => a.DotnetProject ?? "(repo)").Order()
            .ShouldBe(["(repo)", "Demo.Api", "Demo.Core", "Demo.Jobs"]);
    }

    /// <summary>
    /// With no API key the hosted provider is never called (it would answer 401 once per
    /// project); the step logs and returns without storing an analysis.
    /// </summary>
    [Fact]
    public async Task AnalyzeHealthAsync_ProviderWithoutApiKey_SkipsWithoutCallingTheModel()
    {
        var store = new InMemoryGraphStore();
        foreach (var dotnetProject in new[] { "Demo.Api", null })
        {
            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = "Demo", DotnetProject = dotnetProject, OverallHealth = 6.0, TotalFiles = 1
            });
        }
        await store.UpsertFileMetricsBatchAsync("Demo",
            [new FileMetricsEntity { Project = "Demo", DotnetProject = "Demo.Api", FilePath = "Demo.Api/A.cs" }]);
        var options = new AnalysisOptions { DefaultProvider = "anthropic" };
        options.Anthropic.ApiKey = "";
        var handler = new CountingHandler();
        var anthropic = new AnthropicAnalysisProvider(
            new SingleClientFactory(new HttpClient(handler)),
            new AnthropicCircuitBreaker(NullLogger<AnthropicCircuitBreaker>.Instance),
            Options.Create(options),
            NullLogger<AnthropicAnalysisProvider>.Instance);

        var analyzer = new VitalsAnalyzer(
            store,
            new FixedProviderRegistry(anthropic),
            Options.Create(options),
            new LocalFileSystem(),
            new CompositeLintRunner(new LintResultCache(), new NoOpLintRunner(), NullLogger<CompositeLintRunner>.Instance),
            new LintResultCache(),
            new DiagnosticDetailCache(),
            NullLogger<VitalsAnalyzer>.Instance);

        await analyzer.AnalyzeHealthAsync("Demo");

        handler.Requests.ShouldBe(0);
        (await store.GetProjectHealthAnalysesAsync("Demo")).ShouldBeEmpty();

        // With a key the same configuration calls the model.
        options.Anthropic.ApiKey = "test-key";
        await analyzer.AnalyzeHealthAsync("Demo");
        handler.Requests.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("", "https://api.openai.com/v1", false)]
    [InlineData("sk-test", "https://api.openai.com/v1", true)]
    [InlineData("", "http://localhost:8000/v1", true)]
    public async Task OpenAiProvider_IsConfiguredOnlyWithAKeyForTheHostedApi(string apiKey, string baseUrl, bool expected)
    {
        var options = new AnalysisOptions();
        options.OpenAi.ApiKey = apiKey;
        options.OpenAi.BaseUrl = baseUrl;
        var provider = new OpenAiAnalysisProvider(
            new SingleClientFactory(new HttpClient(new CountingHandler())),
            Options.Create(options),
            NullLogger<OpenAiAnalysisProvider>.Instance);

        (await provider.IsConfiguredAsync()).ShouldBe(expected);
    }

    private sealed class FixedProviderRegistry(IAnalysisModelProvider provider) : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => provider;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"unauthorized\"}")
            });
        }
    }

    private sealed class ConcurrencyDetectingGraphStore : InMemoryGraphStore
    {
        private int _activeOperations;

        public bool OverlapDetected { get; private set; }

        public override Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string project, string? dotnetProject = null) =>
            TrackAsync(() => base.GetFileMetricsAsync(project, dotnetProject));

        public override Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis) =>
            TrackAsync(async () =>
            {
                await base.UpsertProjectHealthAnalysisAsync(analysis);
                return true;
            });

        private async Task<T> TrackAsync<T>(Func<Task<T>> operation)
        {
            if (Interlocked.Increment(ref _activeOperations) > 1)
                OverlapDetected = true;
            try
            {
                await Task.Delay(20);
                return await operation();
            }
            finally
            {
                Interlocked.Decrement(ref _activeOperations);
            }
        }
    }

    private sealed class NoOpLintRunner : ILintRunner
    {
        public Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
    }

    private sealed class SlowAnalysisProviderRegistry : IAnalysisProviderRegistry
    {
        private readonly SlowProvider _provider = new();

        public IAnalysisModelProvider GetProvider(string? providerName = null) => _provider;

        private sealed class SlowProvider : IAnalysisModelProvider
        {
            public string ProviderName => "slow";

            public AnalysisProviderCapabilities Capabilities { get; } = new(false, false, false, false);

            public async Task<AnalysisTextResponse> ExecuteAsync(
                AnalysisPrompt prompt, AnalysisRequestOptions request, CancellationToken ct = default)
            {
                await Task.Delay(Random.Shared.Next(5, 40), ct);
                return new AnalysisTextResponse("Healthy.", "test-model", ProviderName);
            }

            public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
                IReadOnlyList<AnalysisBatchRequestItem> items, AnalysisRequestOptions request, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
                string batchId, IReadOnlyList<string>? requestIds = null, CancellationToken ct = default) =>
                throw new NotSupportedException();
        }
    }
}
