using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Tests.Extractors;

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
