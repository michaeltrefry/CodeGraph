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

    private static async Task RunGitAsync(string repoPath, string arguments, params (string Name, string Value)[] environment)
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

    private sealed class StubAnalysisProviderRegistry : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => throw new NotSupportedException();
    }
}
