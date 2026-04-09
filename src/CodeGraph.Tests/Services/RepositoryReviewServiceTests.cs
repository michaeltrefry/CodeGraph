using System.Diagnostics;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Reviews;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class RepositoryReviewServiceTests
{
    [Fact]
    public async Task StartReviewAsync_UpdateMode_ContinuesInterruptedUpdateReviewWithSameBaseline()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            InitializeGitRepository(repoPath);
            await SeedRepositoryAsync(store, repoPath);
            CommitAll(repoPath, "baseline");
            var baselineCommitSha = GetGitOutput(repoPath, "rev-parse HEAD");

            var baselineProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectSynthesisJson("API final", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectWorkflowJson("Worker workflow", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                """
                {
                  "overview": "Baseline repository review.",
                  "strengths": ["Projects are separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": [],
                  "followUps": ["Tighten failure handling in the worker path."]
                }
                """);
            var baselineProjectService = CreateProjectService(store, baselineProvider);
            var baselineRepositoryService = CreateRepositoryService(store, baselineProvider, baselineProjectService);

            var baselineRunId = await baselineRepositoryService.StartReviewAsync("TestRepo", "full");
            await baselineRepositoryService.ExecuteReviewRunAsync(baselineRunId);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Api", "FooService.cs"), """
                namespace Example.Api;

                public class FooService
                {
                    public string Execute(string? input)
                    {
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            return "fallback";
                        }

                        return input.Trim();
                    }
                }
                """);
            CommitAll(repoPath, "api change");
            var updatedCommitSha = GetGitOutput(repoPath, "rev-parse HEAD");

            var interruptedRunId = await store.CreateRepositoryReviewRunAsync(new RepositoryReviewRunEntity
            {
                Repo = "TestRepo",
                ReviewedCommitSha = updatedCommitSha,
                BaselineReviewRunId = baselineRunId,
                BaselineCommitSha = baselineCommitSha,
                Status = "interrupted",
                ReviewMode = "update",
                PromptVersion = RepositoryReviewService.CurrentPromptVersion,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = DateTime.UtcNow,
                Error = "Repository review was interrupted while the API was restarting. Continue Review to restart it."
            });

            var repositoryService = CreateRepositoryService(store, new RecordingProvider(), CreateProjectService(store, new RecordingProvider()));
            var continuedRunId = await repositoryService.StartReviewAsync("TestRepo", "update");
            continuedRunId.ShouldNotBe(interruptedRunId);

            var continuedRun = await store.GetRepositoryReviewRunAsync(continuedRunId);
            continuedRun.ShouldNotBeNull();
            continuedRun!.ReviewMode.ShouldBe("update");
            continuedRun.BaselineReviewRunId.ShouldBe(baselineRunId);
            continuedRun.BaselineCommitSha.ShouldBe(baselineCommitSha);
            continuedRun.ReviewedCommitSha.ShouldBe(updatedCommitSha);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task RepositoryReviewRecoveryService_MarksQueuedAndRunningRunsInterrupted()
    {
        var store = new InMemoryGraphStore();

        var queuedRunId = await store.CreateRepositoryReviewRunAsync(new RepositoryReviewRunEntity
        {
            Repo = "QueuedRepo",
            Status = "queued",
            ReviewMode = "full",
            PromptVersion = RepositoryReviewService.CurrentPromptVersion,
            CreatedAt = DateTime.UtcNow.AddMinutes(-3)
        });

        var runningRunId = await store.CreateRepositoryReviewRunAsync(new RepositoryReviewRunEntity
        {
            Repo = "RunningRepo",
            Status = "running",
            ReviewMode = "update",
            PromptVersion = RepositoryReviewService.CurrentPromptVersion,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-2)
        });

        var completedRunId = await store.CreateRepositoryReviewRunAsync(new RepositoryReviewRunEntity
        {
            Repo = "CompletedRepo",
            Status = "completed",
            ReviewMode = "full",
            PromptVersion = RepositoryReviewService.CurrentPromptVersion,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var recoveryService = new RepositoryReviewRecoveryService(
            store,
            NullLogger<RepositoryReviewRecoveryService>.Instance);

        await recoveryService.RecoverInterruptedRunsAsync();

        var queuedRun = await store.GetRepositoryReviewRunAsync(queuedRunId);
        queuedRun.ShouldNotBeNull();
        queuedRun!.Status.ShouldBe("interrupted");
        queuedRun.Error.ShouldNotBeNull();
        queuedRun.Error.ShouldContain("queued when the API restarted");

        var runningRun = await store.GetRepositoryReviewRunAsync(runningRunId);
        runningRun.ShouldNotBeNull();
        runningRun!.Status.ShouldBe("interrupted");
        runningRun.Error.ShouldNotBeNull();
        runningRun.Error.ShouldContain("interrupted while the API was restarting");

        var completedRun = await store.GetRepositoryReviewRunAsync(completedRunId);
        completedRun.ShouldNotBeNull();
        completedRun!.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task ExecuteReviewRunAsync_PersistsRepositoryReviewWithProjectSections()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            await SeedRepositoryAsync(store, repoPath);

            var provider = new RecordingProvider(
                ProjectWorkflowJson("API workflow", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectSynthesisJson("API final", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectWorkflowJson("Worker workflow", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                """
                {
                  "overview": "The repository has useful separation between API and worker concerns, but both reviewed projects still contain reliability and design risks that should be tightened.",
                  "strengths": ["Projects are separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": ["Lower-priority files outside the review budget were not inspected."],
                  "followUps": ["Tighten failure handling in the worker path."]
                }
                """);

            var projectReviewService = CreateProjectService(store, provider);
            var repositoryReviewService = CreateRepositoryService(store, provider, projectReviewService);

            var runId = await repositoryReviewService.StartReviewAsync("TestRepo", "full");
            await repositoryReviewService.ExecuteReviewRunAsync(runId);

            var latest = await repositoryReviewService.GetLatestReviewAsync("TestRepo");
            latest.ShouldNotBeNull();
            latest!.Run.ReviewedCommitSha.ShouldBe("feedfacefeedfacefeedfacefeedfacefeedface");
            latest.ProjectReviews.Count.ShouldBe(2);
            latest.ProjectReviews.Select(p => p.ProjectName).ShouldBe(["TestRepo.Api", "TestRepo.Worker"], ignoreOrder: true);
            latest.Findings.Count.ShouldBe(2);
            latest.ProjectReviews.Sum(p => p.Findings.Count).ShouldBe(2);
            latest.Overview.ShouldContain("repository");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteReviewRunAsync_UpdateMode_RerunsOnlyImpactedProjectsAndReusesBaselineSections()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            InitializeGitRepository(repoPath);
            await SeedRepositoryAsync(store, repoPath);
            CommitAll(repoPath, "baseline");

            var baselineProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectSynthesisJson("API final", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectWorkflowJson("Worker workflow", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                """
                {
                  "overview": "Baseline repository review.",
                  "strengths": ["Projects are separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": [],
                  "followUps": ["Tighten failure handling in the worker path."]
                }
                """);
            var baselineProjectService = CreateProjectService(store, baselineProvider);
            var baselineRepositoryService = CreateRepositoryService(store, baselineProvider, baselineProjectService);

            var baselineRunId = await baselineRepositoryService.StartReviewAsync("TestRepo", "full");
            await baselineRepositoryService.ExecuteReviewRunAsync(baselineRunId);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Api", "FooService.cs"), """
                namespace Example.Api;

                public class FooService
                {
                    public string Execute(string? input)
                    {
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            return "fallback";
                        }

                        return input.Trim();
                    }
                }
                """);
            CommitAll(repoPath, "api change");

            var updateProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                ProjectSynthesisJson("API final updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                """
                {
                  "overview": "Updated repository review with one refreshed project and one carried-forward baseline section.",
                  "strengths": ["Projects remain separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": ["Worker section reused from baseline review."],
                  "followUps": ["Recheck the worker path if shared abstractions change later."]
                }
                """);
            var updateProjectService = CreateProjectService(store, updateProvider);
            var updateRepositoryService = CreateRepositoryService(store, updateProvider, updateProjectService);

            var updateRunId = await updateRepositoryService.StartReviewAsync("TestRepo", "update");
            await updateRepositoryService.ExecuteReviewRunAsync(updateRunId);

            var latest = await updateRepositoryService.GetReviewAsync(updateRunId);
            latest.ShouldNotBeNull();
            latest!.Run.ReviewMode.ShouldBe("update");
            latest.ProjectReviews.Count.ShouldBe(2);
            updateProvider.CallCount.ShouldBe(3);

            var apiSection = latest.ProjectReviews.Single(section => section.ProjectName == "TestRepo.Api");
            apiSection.ReusedFromBaseline.ShouldBeFalse();
            apiSection.Overview.ShouldBe("API final updated");

            var workerSection = latest.ProjectReviews.Single(section => section.ProjectName == "TestRepo.Worker");
            workerSection.ReusedFromBaseline.ShouldBeTrue();
            workerSection.Overview.ShouldBe("Worker final");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteReviewRunAsync_UpdateMode_BroadConfigChangeFallsBackToFullReview()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            InitializeGitRepository(repoPath);
            await SeedRepositoryAsync(store, repoPath);
            CommitAll(repoPath, "baseline");

            var baselineProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectSynthesisJson("API final", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectWorkflowJson("Worker workflow", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                """
                {
                  "overview": "Baseline repository review.",
                  "strengths": ["Projects are separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": [],
                  "followUps": ["Tighten failure handling in the worker path."]
                }
                """);
            var baselineProjectService = CreateProjectService(store, baselineProvider);
            var baselineRepositoryService = CreateRepositoryService(store, baselineProvider, baselineProjectService);

            var baselineRunId = await baselineRepositoryService.StartReviewAsync("TestRepo", "full");
            await baselineRepositoryService.ExecuteReviewRunAsync(baselineRunId);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            CommitAll(repoPath, "config change");

            var updateProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                ProjectSynthesisJson("API final updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                ProjectWorkflowJson("Worker workflow updated", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker retry path still needs guard rails"),
                ProjectSynthesisJson("Worker final updated", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker retry path still needs guard rails"),
                """
                {
                  "overview": "Broad config changes forced a full repository refresh.",
                  "strengths": ["Projects remain separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker"],
                  "skippedAreas": [],
                  "followUps": ["Revalidate both projects after broad config changes."]
                }
                """);
            var updateProjectService = CreateProjectService(store, updateProvider);
            var updateRepositoryService = CreateRepositoryService(store, updateProvider, updateProjectService);

            var updateRunId = await updateRepositoryService.StartReviewAsync("TestRepo", "update");
            await updateRepositoryService.ExecuteReviewRunAsync(updateRunId);

            var latest = await updateRepositoryService.GetReviewAsync(updateRunId);
            latest.ShouldNotBeNull();
            latest!.ProjectReviews.All(section => !section.ReusedFromBaseline).ShouldBeTrue();
            updateProvider.CallCount.ShouldBe(5);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteReviewRunAsync_UpdateMode_ExpandsBlastRadiusAcrossGraphConnectedProjects()
    {
        var store = new InMemoryGraphStore();
        var repoPath = CreateTempRepo("TestRepo");

        try
        {
            InitializeGitRepository(repoPath);
            await SeedRepositoryWithGraphCouplingAsync(store, repoPath);
            CommitAll(repoPath, "baseline");

            var baselineProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectSynthesisJson("API final", "TestRepo.Api", "src/Api/FooService.cs", "API service mixes concerns"),
                ProjectWorkflowJson("Worker workflow", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectWorkflowJson("UI workflow", "TestRepo.Ui", "src/Ui/ViewModel.cs", "UI view model is straightforward"),
                ProjectSynthesisJson("UI final", "TestRepo.Ui", "src/Ui/ViewModel.cs", "UI view model is straightforward"),
                """
                {
                  "overview": "Baseline repository review.",
                  "strengths": ["Projects are separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker", "TestRepo.Ui"],
                  "skippedAreas": [],
                  "followUps": ["Tighten failure handling in the worker path."]
                }
                """);
            var baselineProjectService = CreateProjectService(store, baselineProvider);
            var baselineRepositoryService = CreateRepositoryService(store, baselineProvider, baselineProjectService);

            var baselineRunId = await baselineRepositoryService.StartReviewAsync("TestRepo", "full");
            await baselineRepositoryService.ExecuteReviewRunAsync(baselineRunId);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Api", "FooService.cs"), """
                namespace Example.Api;

                public class FooService
                {
                    public string Execute(string? input)
                    {
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            return "fallback";
                        }

                        return input.Trim();
                    }
                }
                """);
            CommitAll(repoPath, "api change");

            var updateProvider = new RecordingProvider(
                ProjectWorkflowJson("API workflow updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                ProjectSynthesisJson("API final updated", "TestRepo.Api", "src/Api/FooService.cs", "API fallback behavior changed"),
                ProjectWorkflowJson("Worker workflow updated", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                ProjectSynthesisJson("Worker final updated", "TestRepo.Worker", "src/Worker/JobRunner.cs", "Worker job runner has weak error handling"),
                """
                {
                  "overview": "Updated repository review with graph-expanded worker coverage.",
                  "strengths": ["Projects remain separated by responsibility."],
                  "reviewedAreas": ["TestRepo.Api", "TestRepo.Worker", "TestRepo.Ui"],
                  "skippedAreas": ["UI section reused from baseline review."],
                  "followUps": ["Recheck the worker path if the API contract changes further."]
                }
                """);
            var updateProjectService = CreateProjectService(store, updateProvider);
            var updateRepositoryService = CreateRepositoryService(store, updateProvider, updateProjectService);

            var updateRunId = await updateRepositoryService.StartReviewAsync("TestRepo", "update");
            await updateRepositoryService.ExecuteReviewRunAsync(updateRunId);

            var latest = await updateRepositoryService.GetReviewAsync(updateRunId);
            latest.ShouldNotBeNull();
            latest!.Run.ReviewMode.ShouldBe("update");
            updateProvider.CallCount.ShouldBe(5);

            latest.ProjectReviews.Single(section => section.ProjectName == "TestRepo.Api").ReusedFromBaseline.ShouldBeFalse();
            latest.ProjectReviews.Single(section => section.ProjectName == "TestRepo.Worker").ReusedFromBaseline.ShouldBeFalse();
            latest.ProjectReviews.Single(section => section.ProjectName == "TestRepo.Ui").ReusedFromBaseline.ShouldBeTrue();

            updateProvider.Prompts.Any(prompt =>
                prompt.Contains("Project: TestRepo.Worker", StringComparison.Ordinal) &&
                prompt.Contains("src/Worker/JobRunner.cs", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    private static RepositoryReviewService CreateRepositoryService(
        InMemoryGraphStore store,
        RecordingProvider provider,
        ProjectReviewService projectReviewService)
    {
        return new RepositoryReviewService(
            store,
            new SingleProviderRegistry(provider),
            projectReviewService,
            new NoOpRepositoryBackgroundRunner(),
            Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions
            {
                Review = new ReviewOptions
                {
                    MaxFilesToInspect = 5,
                    MaxFindings = 10
                }
            }),
            NullLogger<RepositoryReviewService>.Instance);
    }

    private static ProjectReviewService CreateProjectService(InMemoryGraphStore store, RecordingProvider provider)
    {
        var fileSystem = new LocalFileSystem();
        return new ProjectReviewService(
            store,
            new SingleProviderRegistry(provider),
            fileSystem,
            new FileSystemSourceFileProvider(fileSystem),
            new NoOpProjectBackgroundRunner(),
            Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions
            {
                Review = new ReviewOptions
                {
                    MaxFilesToInspect = 5,
                    MaxFindings = 10
                }
            }),
            NullLogger<ProjectReviewService>.Instance);
    }

    private static async Task SeedRepositoryAsync(InMemoryGraphStore store, string repoPath)
    {
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "TestRepo",
            LocalPath = repoPath,
            LastCommitSha = "feedfacefeedfacefeedfacefeedfacefeedface",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        Directory.CreateDirectory(Path.Combine(repoPath, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(repoPath, "src", "Worker"));

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Api", "FooService.cs"), """
            namespace Example.Api;

            public class FooService
            {
                public string Execute(string? input)
                {
                    return input ?? string.Empty;
                }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Worker", "JobRunner.cs"), """
            namespace Example.Worker;

            public class JobRunner
            {
                public void Run(bool fail)
                {
                    if (fail)
                    {
                        try
                        {
                            throw new InvalidOperationException("boom");
                        }
                        catch
                        {
                        }
                    }
                }
            }
            """);

        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.File,
            Name = "FooService.cs",
            QualifiedName = "src/Api/FooService.cs",
            FilePath = "src/Api/FooService.cs"
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.Class,
            Name = "FooService",
            QualifiedName = "Example.Api.FooService",
            FilePath = "src/Api/FooService.cs",
            StartLine = 3,
            EndLine = 9
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Worker",
            Label = NodeLabel.File,
            Name = "JobRunner.cs",
            QualifiedName = "src/Worker/JobRunner.cs",
            FilePath = "src/Worker/JobRunner.cs"
        });
        store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Worker",
            Label = NodeLabel.Class,
            Name = "JobRunner",
            QualifiedName = "Example.Worker.JobRunner",
            FilePath = "src/Worker/JobRunner.cs",
            StartLine = 3,
            EndLine = 16
        });

        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis("TestRepo", "TestRepo.Api", "API layer.", ConfidenceLevel.Medium, [], [], [], [], "test-model", DateTime.UtcNow));
        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis("TestRepo", "TestRepo.Worker", "Worker layer.", ConfidenceLevel.Medium, [], [], [], [], "test-model", DateTime.UtcNow));

        await store.UpsertFileMetricsBatchAsync("TestRepo",
        [
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Api",
                FilePath = "src/Api/FooService.cs",
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
                DotnetProject = "TestRepo.Worker",
                FilePath = "src/Worker/JobRunner.cs",
                ComplexityScore = 16,
                LongestFunction = 12,
                HealthScore = 5.0,
                RiskScore = 7.0,
                LintErrors = 0,
                LintWarnings = 0,
                ComputedAt = DateTime.UtcNow
            }
        ]);
    }

    private static async Task SeedRepositoryWithGraphCouplingAsync(InMemoryGraphStore store, string repoPath)
    {
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "TestRepo",
            LocalPath = repoPath,
            LastCommitSha = "feedfacefeedfacefeedfacefeedfacefeedface",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        Directory.CreateDirectory(Path.Combine(repoPath, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(repoPath, "src", "Worker"));
        Directory.CreateDirectory(Path.Combine(repoPath, "src", "Ui"));

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Api", "FooService.cs"), """
            namespace Example.Api;

            public class FooService
            {
                public string Execute(string? input)
                {
                    return input ?? string.Empty;
                }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Worker", "JobRunner.cs"), """
            namespace Example.Worker;

            public class JobRunner
            {
                public void Run()
                {
                }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "Ui", "ViewModel.cs"), """
            namespace Example.Ui;

            public class ViewModel
            {
                public string Title => "hello";
            }
            """);

        var apiFileId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.File,
            Name = "FooService.cs",
            QualifiedName = "src/Api/FooService.cs",
            FilePath = "src/Api/FooService.cs"
        });
        var apiClassId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Api",
            Label = NodeLabel.Class,
            Name = "FooService",
            QualifiedName = "Example.Api.FooService",
            FilePath = "src/Api/FooService.cs",
            StartLine = 3,
            EndLine = 9
        });
        var workerFileId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Worker",
            Label = NodeLabel.File,
            Name = "JobRunner.cs",
            QualifiedName = "src/Worker/JobRunner.cs",
            FilePath = "src/Worker/JobRunner.cs"
        });
        var workerClassId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Worker",
            Label = NodeLabel.Class,
            Name = "JobRunner",
            QualifiedName = "Example.Worker.JobRunner",
            FilePath = "src/Worker/JobRunner.cs",
            StartLine = 3,
            EndLine = 8
        });
        var uiFileId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Ui",
            Label = NodeLabel.File,
            Name = "ViewModel.cs",
            QualifiedName = "src/Ui/ViewModel.cs",
            FilePath = "src/Ui/ViewModel.cs"
        });
        var uiClassId = store.AddNode(new GraphNode
        {
            Project = "TestRepo",
            DotnetProject = "TestRepo.Ui",
            Label = NodeLabel.Class,
            Name = "ViewModel",
            QualifiedName = "Example.Ui.ViewModel",
            FilePath = "src/Ui/ViewModel.cs",
            StartLine = 3,
            EndLine = 6
        });

        store.AddEdge(new GraphEdge
        {
            Project = "TestRepo",
            SourceId = apiClassId,
            TargetId = workerClassId,
            Type = EdgeType.CALLS
        });
        store.AddEdge(new GraphEdge
        {
            Project = "TestRepo",
            SourceId = apiFileId,
            TargetId = apiClassId,
            Type = EdgeType.DEFINES
        });
        store.AddEdge(new GraphEdge
        {
            Project = "TestRepo",
            SourceId = workerFileId,
            TargetId = workerClassId,
            Type = EdgeType.DEFINES
        });
        store.AddEdge(new GraphEdge
        {
            Project = "TestRepo",
            SourceId = uiFileId,
            TargetId = uiClassId,
            Type = EdgeType.DEFINES
        });

        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis("TestRepo", "TestRepo.Api", "API layer.", ConfidenceLevel.Medium, [], [], [], [], "test-model", DateTime.UtcNow));
        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis("TestRepo", "TestRepo.Worker", "Worker layer.", ConfidenceLevel.Medium, [], [], [], [], "test-model", DateTime.UtcNow));
        await store.UpsertProjectAnalysisAsync("TestRepo",
            new StoredProjectAnalysis("TestRepo", "TestRepo.Ui", "UI layer.", ConfidenceLevel.Medium, [], [], [], [], "test-model", DateTime.UtcNow));

        await store.UpsertFileMetricsBatchAsync("TestRepo",
        [
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Api",
                FilePath = "src/Api/FooService.cs",
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
                DotnetProject = "TestRepo.Worker",
                FilePath = "src/Worker/JobRunner.cs",
                ComplexityScore = 16,
                LongestFunction = 10,
                HealthScore = 5.0,
                RiskScore = 7.0,
                LintErrors = 0,
                LintWarnings = 0,
                ComputedAt = DateTime.UtcNow
            },
            new FileMetricsEntity
            {
                Project = "TestRepo",
                DotnetProject = "TestRepo.Ui",
                FilePath = "src/Ui/ViewModel.cs",
                ComplexityScore = 2,
                LongestFunction = 2,
                HealthScore = 8.0,
                RiskScore = 1.0,
                LintErrors = 0,
                LintWarnings = 0,
                ComputedAt = DateTime.UtcNow
            }
        ]);
    }

    private static string ProjectWorkflowJson(string overview, string projectName, string filePath, string title) =>
        $$"""
        {
          "overview": "{{overview}}",
          "strengths": ["{{projectName}} keeps a clear surface area."],
          "reviewedAreas": ["{{projectName}}"],
          "skippedAreas": [],
          "followUps": ["Add deeper tests around {{projectName}} edge cases."],
          "candidateFindings": [
            {
              "severity": "medium",
              "category": "design",
              "title": "{{title}}",
              "explanation": "The reviewed code combines responsibilities in a way that will be harder to maintain.",
              "evidence": "The main class handles more than one concern in the inspected file.",
              "filePath": "{{filePath}}",
              "lineStart": 3,
              "lineEnd": 9,
              "suggestedImprovement": "Split the risky behavior into smaller units with clearer ownership.",
              "confidence": "high"
            }
          ]
        }
        """;

    private static string ProjectSynthesisJson(string overview, string projectName, string filePath, string title) =>
        $$"""
        {
          "overview": "{{overview}}",
          "strengths": ["{{projectName}} keeps a clear surface area."],
          "reviewedAreas": ["{{projectName}}"],
          "skippedAreas": [],
          "followUps": ["Add deeper tests around {{projectName}} edge cases."],
          "findings": [
            {
              "severity": "medium",
              "category": "design",
              "title": "{{title}}",
              "explanation": "The reviewed code combines responsibilities in a way that will be harder to maintain.",
              "evidence": "The main class handles more than one concern in the inspected file.",
              "filePath": "{{filePath}}",
              "lineStart": 3,
              "lineEnd": 9,
              "suggestedImprovement": "Split the risky behavior into smaller units with clearer ownership.",
              "confidence": "high"
            }
          ]
        }
        """;

    private static string CreateTempRepo(string repoName)
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-repository-review-tests", Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, repoName);
        Directory.CreateDirectory(repoPath);
        return repoPath;
    }

    private static void InitializeGitRepository(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config user.name \"CodeGraph Tests\"");
        RunGit(repoPath, "config user.email \"codegraph-tests@example.com\"");
    }

    private static void CommitAll(string repoPath, string message)
    {
        RunGit(repoPath, "add .");
        RunGit(repoPath, $"commit -m \"{message}\"");
    }

    private static string GetGitOutput(string repoPath, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"{stdout}\n{stderr}");
        return stdout.Trim();
    }

    private static void RunGit(string repoPath, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();
        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"{stdout}\n{stderr}");
    }

    private sealed class SingleProviderRegistry(RecordingProvider provider) : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => provider;
    }

    private sealed class RecordingProvider(params string[] responses) : IAnalysisModelProvider
    {
        private readonly Queue<string> responsesQueue = new(responses);
        public int CallCount { get; private set; }
        public List<string> Prompts { get; } = [];

        public string ProviderName => "test";
        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            CallCount++;
            Prompts.Add(prompt.UserPrompt);
            return Task.FromResult(new AnalysisTextResponse(responsesQueue.Dequeue(), "test-model", ProviderName));
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

    private sealed class NoOpProjectBackgroundRunner : IProjectReviewBackgroundRunner
    {
        public Task EnqueueAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpRepositoryBackgroundRunner : IRepositoryReviewBackgroundRunner
    {
        public Task EnqueueAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
