using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Reviews;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class ProjectReviewServiceTests
{
    [Fact]
    public async Task StartReviewAsync_UsesDiagnosticsToSeedInspectionQueue()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            await SeedProjectAsync(store, repoPath);
            await store.UpsertFileMetricsBatchAsync("TestRepo",
            [
                new FileMetricsEntity
                {
                    Project = "TestRepo",
                    DotnetProject = "TestRepo.Api",
                    FilePath = "src/FooService.cs",
                    ComplexityScore = 12,
                    LongestFunction = 8,
                    HealthScore = 6.0,
                    RiskScore = 5.0,
                    LintErrors = 0,
                    LintWarnings = 1,
                    ComputedAt = DateTime.UtcNow
                },
                new FileMetricsEntity
                {
                    Project = "TestRepo",
                    DotnetProject = "TestRepo.Api",
                    FilePath = "src/BarService.cs",
                    ComplexityScore = 4,
                    LongestFunction = 3,
                    HealthScore = 8.0,
                    RiskScore = 2.0,
                    LintErrors = 0,
                    LintWarnings = 0,
                    ComputedAt = DateTime.UtcNow
                },
                new FileMetricsEntity
                {
                    Project = "TestRepo",
                    DotnetProject = "TestRepo.Api",
                    FilePath = "src/BigService.cs",
                    ComplexityScore = 35,
                    LongestFunction = 23,
                    HealthScore = 5.2,
                    RiskScore = 4.0,
                    LintErrors = 0,
                    LintWarnings = 0,
                    ComputedAt = DateTime.UtcNow
                }
            ]);
            await store.UpsertProjectDiagnosticsBatchAsync("TestRepo",
            [
                new ProjectDiagnosticEntity
                {
                    Project = "TestRepo",
                    DotnetProject = "TestRepo.Api",
                    Source = "roslyn",
                    DiagnosticKey = "foo-warning",
                    DiagnosticId = "CS8602",
                    Severity = "warning",
                    Message = "Possible null dereference",
                    FilePath = "src/FooService.cs",
                    LineStart = 12,
                    LineEnd = 12,
                    ComputedAt = DateTime.UtcNow
                }
            ]);

            var provider = new RecordingProvider(
                """
                {
                  "overview": "workflow",
                  "strengths": [],
                  "reviewedAreas": [],
                  "skippedAreas": [],
                  "followUps": [],
                  "candidateFindings": []
                }
                """,
                """
                {
                  "overview": "final",
                  "strengths": [],
                  "reviewedAreas": [],
                  "skippedAreas": [],
                  "followUps": [],
                  "findings": []
                }
                """);

            var service = CreateService(store, provider, maxFilesToInspect: 1);

            var runId = await service.StartReviewAsync("TestRepo", "TestRepo.Api", "standard");
            await service.ExecuteReviewRunAsync(runId);

            provider.Prompts.Count.ShouldBe(2);
            provider.Prompts[0].ShouldContain("### src/FooService.cs");
            provider.Prompts[0].ShouldNotContain("### src/BarService.cs");

            var latest = await service.GetLatestReviewAsync("TestRepo", "TestRepo.Api");
            latest.ShouldNotBeNull();
            latest!.Run.ReviewedCommitSha.ShouldBe("feedfacefeedfacefeedfacefeedfacefeedface");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task StartReviewAsync_DoesNotTurnDiagnosticsIntoAutomaticFindings()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            await SeedProjectAsync(store, repoPath);
            await store.UpsertProjectDiagnosticsBatchAsync("TestRepo",
            [
                new ProjectDiagnosticEntity
                {
                    Project = "TestRepo",
                    DotnetProject = "TestRepo.Api",
                    Source = "roslyn",
                    DiagnosticKey = "foo-warning",
                    DiagnosticId = "CS8602",
                    Severity = "warning",
                    Message = "Possible null dereference",
                    FilePath = "src/FooService.cs",
                    LineStart = 12,
                    LineEnd = 12,
                    ComputedAt = DateTime.UtcNow
                }
            ]);

            var provider = new RecordingProvider(
                """
                {
                  "overview": "workflow",
                  "strengths": ["Uses clear file organization"],
                  "reviewedAreas": ["FooService"],
                  "skippedAreas": [],
                  "followUps": ["Check additional null flows"],
                  "candidateFindings": []
                }
                """,
                """
                {
                  "overview": "final",
                  "strengths": ["Uses clear file organization"],
                  "reviewedAreas": ["FooService"],
                  "skippedAreas": [],
                  "followUps": ["Check additional null flows"],
                  "findings": []
                }
                """);

            var service = CreateService(store, provider);

            var runId = await service.StartReviewAsync("TestRepo", "TestRepo.Api", "standard");
            await service.ExecuteReviewRunAsync(runId);
            var findings = await store.GetProjectReviewFindingsAsync(runId);

            findings.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task StartReviewAsync_PersistsFindingsForRiskyFilesWithoutDiagnostics()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            await SeedProjectAsync(store, repoPath);

            var provider = new RecordingProvider(
                """
                {
                  "overview": "workflow",
                  "strengths": [],
                  "reviewedAreas": ["BigService"],
                  "skippedAreas": [],
                  "followUps": [],
                  "candidateFindings": [
                    {
                      "severity": "medium",
                      "category": "design",
                      "title": "BigService mixes orchestration and business rules",
                      "explanation": "The class coordinates multiple phases and embeds policy logic in one place.",
                      "evidence": "The Process method spans multiple branches and responsibilities in a single class.",
                      "filePath": "src/BigService.cs",
                      "lineStart": 1,
                      "lineEnd": 23,
                      "suggestedImprovement": "Split orchestration from rule evaluation into smaller collaborators.",
                      "confidence": "high"
                    }
                  ]
                }
                """,
                """
                {
                  "overview": "final",
                  "strengths": [],
                  "reviewedAreas": ["BigService"],
                  "skippedAreas": [],
                  "followUps": [],
                  "findings": [
                    {
                      "severity": "medium",
                      "category": "design",
                      "title": "BigService mixes orchestration and business rules",
                      "explanation": "The class coordinates multiple phases and embeds policy logic in one place.",
                      "evidence": "The Process method spans multiple branches and responsibilities in a single class.",
                      "filePath": "src/BigService.cs",
                      "lineStart": 1,
                      "lineEnd": 23,
                      "suggestedImprovement": "Split orchestration from rule evaluation into smaller collaborators.",
                      "confidence": "high"
                    }
                  ]
                }
                """);

            var service = CreateService(store, provider, maxFilesToInspect: 1);

            var runId = await service.StartReviewAsync("TestRepo", "TestRepo.Api", "standard");
            await service.ExecuteReviewRunAsync(runId);
            var latest = await service.GetLatestReviewAsync("TestRepo", "TestRepo.Api");

            latest.ShouldNotBeNull();
            latest!.Findings.Count.ShouldBe(1);
            latest.Findings[0].FilePath.ShouldBe("src/BigService.cs");
            latest.Findings[0].Evidence.ShouldContain("Process method");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    private static ProjectReviewService CreateService(
        InMemoryGraphStore store,
        RecordingProvider provider,
        int maxFilesToInspect = 3)
    {
        var sourceOptions = Options.Create(new RepositorySourceOptions());
        var analysisOptions = Options.Create(new AnalysisOptions
        {
            Review = new ReviewOptions
            {
                MaxFilesToInspect = maxFilesToInspect,
                MaxSourceCharsPerFile = 10_000,
                MaxFindings = 10
            }
        });

        return new ProjectReviewService(
            store,
            new SingleProviderRegistry(provider),
            new LocalFileSystem(),
            new FileSystemSourceFileProvider(new LocalFileSystem()),
            new NoOpBackgroundRunner(),
            sourceOptions,
            analysisOptions,
            NullLogger<ProjectReviewService>.Instance);
    }

    private static async Task SeedProjectAsync(InMemoryGraphStore store, string repoPath)
    {
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "TestRepo",
            LocalPath = repoPath,
            LastCommitSha = "feedfacefeedfacefeedfacefeedfacefeedface",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        Directory.CreateDirectory(Path.Combine(repoPath, "tests"));

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "FooService.cs"), """
            namespace Example;

            public class FooService
            {
                public string Execute(string? input)
                {
                    return input ?? string.Empty;
                }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "BarService.cs"), """
            namespace Example;

            public class BarService
            {
                public int Count() => 42;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "BigService.cs"), """
            namespace Example;

            public class BigService
            {
                public void Process(bool first, bool second, bool third)
                {
                    if (first)
                    {
                        if (second)
                        {
                            if (third)
                            {
                                System.Console.WriteLine("all");
                            }
                            else
                            {
                                System.Console.WriteLine("partial");
                            }
                        }
                        else
                        {
                            System.Console.WriteLine("fallback");
                        }
                    }
                }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "tests", "BigServiceTests.cs"), """
            public class BigServiceTests {}
            """);

        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.File,
            Name = "FooService.cs",
            QualifiedName = "src/FooService.cs",
            FilePath = "src/FooService.cs"
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.File,
            Name = "BarService.cs",
            QualifiedName = "src/BarService.cs",
            FilePath = "src/BarService.cs"
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.File,
            Name = "BigService.cs",
            QualifiedName = "src/BigService.cs",
            FilePath = "src/BigService.cs"
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.Class,
            Name = "BigService",
            QualifiedName = "Example.BigService",
            FilePath = "src/BigService.cs",
            StartLine = 3,
            EndLine = 24
        });

        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis(
                "TestRepo",
                "TestRepo.Api",
                "Provides application services for the test repo.",
                ConfidenceLevel.Medium,
                [],
                [],
                [],
                [],
                "test-model",
                DateTime.UtcNow));

        await store.UpsertFileMetricsBatchAsync("TestRepo",
        [
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Api",
                FilePath = "src/FooService.cs",
                ComplexityScore = 12,
                LongestFunction = 8,
                HealthScore = 6.0,
                RiskScore = 6.0,
                LintErrors = 0,
                LintWarnings = 1,
                ComputedAt = DateTime.UtcNow
            },
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Api",
                FilePath = "src/BarService.cs",
                ComplexityScore = 4,
                LongestFunction = 3,
                HealthScore = 8.0,
                RiskScore = 2.0,
                LintErrors = 0,
                LintWarnings = 0,
                ComputedAt = DateTime.UtcNow
            },
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Api",
                FilePath = "src/BigService.cs",
                ComplexityScore = 70,
                LongestFunction = 23,
                HealthScore = 3.2,
                RiskScore = 9.5,
                LintErrors = 0,
                LintWarnings = 0,
                ComputedAt = DateTime.UtcNow
            }
        ]);
    }

    private static string CreateTempRepo(string repoName)
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-review-tests", Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, repoName);
        Directory.CreateDirectory(repoPath);
        return repoPath;
    }

    private sealed class SingleProviderRegistry(RecordingProvider provider) : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => provider;
    }

    private sealed class RecordingProvider(params string[] responses) : IAnalysisModelProvider
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Prompts { get; } = [];

        public string ProviderName => "test";
        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            Prompts.Add(prompt.UserPrompt);
            return Task.FromResult(new AnalysisTextResponse(_responses.Dequeue(), "test-model", ProviderName));
        }

        public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
            IReadOnlyList<AnalysisBatchRequestItem> items,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
            string batchId,
            IReadOnlyList<string>? requestIds = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NoOpBackgroundRunner : IProjectReviewBackgroundRunner
    {
        public Task EnqueueAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
