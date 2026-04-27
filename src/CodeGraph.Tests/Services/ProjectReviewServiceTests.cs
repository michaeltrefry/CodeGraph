using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Prompts;
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
    public async Task ReviewRun_UsesDbBackedReviewSettings()
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
            var service = CreateService(
                store,
                provider,
                reviewSettingsResolver: new FixedReviewSettingsResolver(new LlmReviewRuntimeConfig(
                    "openai",
                    "gpt-review",
                    1,
                    10_000,
                    4,
                    10,
                    UpdatedBy: null,
                    UpdatedAtUtc: null,
                    HasDbConfig: true)));

            var runId = await service.StartReviewAsync("TestRepo", "TestRepo.Api", "standard");
            await service.ExecuteReviewRunAsync(runId);

            var run = await store.GetProjectReviewRunAsync(runId);
            run.ShouldNotBeNull();
            run.ModelUsed.ShouldBe("gpt-review");
            provider.Requests.Count.ShouldBe(2);
            provider.Requests.ShouldAllBe(request => request.Model == "gpt-review");
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
    public async Task ExecuteReviewRunAsync_UsesAdminPromptOverrides_ForWorkflowAndSynthesisPrompts()
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

            var promptService = new TestAgentPromptService(new Dictionary<string, string>
            {
                [AgentPromptCatalog.CodeReviewWorkflowSystemPromptKey] = "custom workflow prompt",
                [AgentPromptCatalog.CodeReviewSynthesisSystemPromptKey] = "custom synthesis prompt"
            });
            var service = CreateService(store, provider, promptService: promptService);

            var runId = await service.StartReviewAsync("TestRepo", "TestRepo.Api", "standard");
            await service.ExecuteReviewRunAsync(runId);

            provider.SystemPrompts.ShouldBe(["custom workflow prompt", "custom synthesis prompt"]);
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

    [Fact]
    public async Task StartReviewAsync_NormalizesVerboseFinalReviewForRepoDetailSurface()
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
                  "reviewedAreas": [],
                  "skippedAreas": [],
                  "followUps": [],
                  "candidateFindings": [
                    {
                      "severity": "medium",
                      "category": "design",
                      "title": "BigService mixes orchestration and business rules in one place while also owning multiple unrelated responsibilities that make the class harder to reason about during changes",
                      "explanation": "The Process method coordinates several branches and business decisions in one method. That makes it harder to understand which behavior changes are safe and which branches are supposed to remain coupled over time. The overall shape reads like a single place where more responsibilities will accumulate.",
                      "evidence": "The Process method contains nested conditionals that combine orchestration flow with domain decisions and fallback behavior in one location. The method already spans multiple branches, prints different outcomes, and gives no smaller seams for understanding or testing each phase independently. This is visible directly in the inspected source.",
                      "filePath": "src/BigService.cs",
                      "lineStart": 1,
                      "lineEnd": 23,
                      "suggestedImprovement": "Extract the decision logic into a collaborator and keep BigService focused on orchestration so future changes can be tested and reasoned about in smaller units. As a follow-on, add focused tests around the branches that currently live inside Process.",
                      "confidence": "high"
                    }
                  ]
                }
                """,
                """
                {
                  "overview": "This project is generally understandable, but the strongest risk is that BigService concentrates orchestration and policy decisions in one method, which makes behavioral changes harder to reason about and test. Aside from that, the inspected slice looks stable, though future review passes should keep an eye on whether additional logic continues to accumulate in the same class.",
                  "strengths": [
                    "File organization is straightforward and the inspected source is easy to locate.",
                    "The project keeps simple helper services small and easy to scan.",
                    "The code avoids unnecessary abstraction in the small files that were inspected.",
                    "There is at least one candidate test file that suggests some coverage exists around higher-risk code paths.",
                    "An extra strength that should be trimmed away by the service normalization because the UI does not need this many bullets."
                  ],
                  "reviewedAreas": [
                    "BigService control flow and responsibility split across its main Process method.",
                    "FooService null-handling behavior in the small helper path.",
                    "Candidate tests that appear related to BigService behavior.",
                    "Repository-level context from metrics and stored project analysis.",
                    "An extra reviewed area that should be trimmed away to keep the panel concise.",
                    "Another extra reviewed area."
                  ],
                  "skippedAreas": [
                    "Lower-priority files outside the inspection budget were not read in this pass.",
                    "Another skipped area that should be trimmed.",
                    "A third skipped area that can remain.",
                    "A fourth skipped area that should be removed."
                  ],
                  "followUps": [
                    "Add tests that lock in the intended outcomes for each Process branch.",
                    "Revisit the class if additional business rules are added to the same method.",
                    "Consider a deeper review of adjacent orchestration code if the class continues to grow.",
                    "Check whether logging or error handling should be more explicit around future failure paths.",
                    "An extra follow-up that should be trimmed."
                  ],
                  "findings": [
                    {
                      "severity": "medium",
                      "category": "design",
                      "title": "BigService mixes orchestration and business rules in one place while also owning multiple unrelated responsibilities that make the class harder to reason about during changes",
                      "explanation": "The Process method coordinates several branches and business decisions in one method. That makes it harder to understand which behavior changes are safe and which branches are supposed to remain coupled over time. The overall shape reads like a single place where more responsibilities will accumulate.",
                      "evidence": "The Process method contains nested conditionals that combine orchestration flow with domain decisions and fallback behavior in one location. The method already spans multiple branches, prints different outcomes, and gives no smaller seams for understanding or testing each phase independently. This is visible directly in the inspected source.",
                      "filePath": "src/BigService.cs",
                      "lineStart": 1,
                      "lineEnd": 23,
                      "suggestedImprovement": "Extract the decision logic into a collaborator and keep BigService focused on orchestration so future changes can be tested and reasoned about in smaller units. As a follow-on, add focused tests around the branches that currently live inside Process.",
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
            latest!.Run.PromptVersion.ShouldBe(ProjectReviewService.CurrentPromptVersion);
            latest.Overview.Length.ShouldBeLessThanOrEqualTo(320);
            latest.Strengths.Count.ShouldBe(4);
            latest.ReviewedAreas.Count.ShouldBe(5);
            latest.SkippedAreas.Count.ShouldBe(3);
            latest.FollowUps.Count.ShouldBe(4);
            latest.Findings[0].Title.Length.ShouldBeLessThanOrEqualTo(123);
            latest.Findings[0].Explanation.Length.ShouldBeLessThanOrEqualTo(263);
            latest.Findings[0].Evidence.Length.ShouldBeLessThanOrEqualTo(263);
            latest.Findings[0].SuggestedImprovement.Length.ShouldBeLessThanOrEqualTo(223);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateReviewAsync_UpdateInput_IncludesBaselineAndFocusedChangedSnippets()
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

            var service = CreateService(store, provider, maxFilesToInspect: 2);
            await service.GenerateReviewAsync(
                "TestRepo",
                "TestRepo.Api",
                new ProjectReviewExecutionInput(
                    "update",
                    SeedFiles: ["src/BigService.cs"],
                    BlastRadiusFiles: ["src/BigService.cs"],
                    ChangedLineSpans: new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["src/BigService.cs"] = [new ProjectReviewLineSpan(12, 12)]
                    },
                    CandidateTests: ["tests/BigServiceTests.cs"],
                    UpdateSummary: "Only the BigService branching logic changed in this update.",
                    BaselineContext: new ProjectReviewBaselineContext(
                        "Baseline review flagged BigService as a design hotspot.",
                        ["Small helper files were easy to scan."],
                        ["BigService orchestration"],
                        ["Add targeted branch tests."],
                        [
                            new ProjectReviewFindingResponse(
                                "medium",
                                "design",
                                "BigService mixes orchestration and policy",
                                "The class coordinates too many branches.",
                                "The Process method combines multiple responsibilities.",
                                "src/BigService.cs",
                                1,
                                23,
                                "Split orchestration from rule evaluation.",
                                "high")
                        ])),
                CancellationToken.None);

            provider.Prompts.Count.ShouldBe(2);
            provider.Prompts[0].ShouldContain("## Update Scope");
            provider.Prompts[0].ShouldContain("Only the BigService branching logic changed in this update.");
            provider.Prompts[0].ShouldContain("## Baseline Review Context");
            provider.Prompts[0].ShouldContain("BigService mixes orchestration and policy");
            provider.Prompts[0].ShouldContain("### src/BigService.cs");
            provider.Prompts[0].ShouldContain("... focus lines");
            provider.Prompts[0].ShouldContain("tests/BigServiceTests.cs");
            provider.Prompts[1].ShouldContain("Mode: update");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    private static ProjectReviewService CreateService(
        InMemoryGraphStore store,
        RecordingProvider provider,
        int maxFilesToInspect = 3,
        IAgentPromptService? promptService = null,
        IDbBackedReviewSettingsResolver? reviewSettingsResolver = null)
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
            NullLogger<ProjectReviewService>.Instance,
            promptService,
            reviewSettingsResolver);
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
        public List<string> SystemPrompts { get; } = [];
        public List<AnalysisRequestOptions> Requests { get; } = [];

        public string ProviderName => "test";
        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            SystemPrompts.Add(prompt.SystemPrompt);
            Prompts.Add(prompt.UserPrompt);
            Requests.Add(request);
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

    private sealed class FixedReviewSettingsResolver(LlmReviewRuntimeConfig config) : IDbBackedReviewSettingsResolver
    {
        public Task<LlmReviewRuntimeConfig> GetReviewAsync(CancellationToken ct = default) => Task.FromResult(config);
    }
}
