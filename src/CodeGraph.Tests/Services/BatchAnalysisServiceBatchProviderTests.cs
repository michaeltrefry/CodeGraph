using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Prompts;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisServiceBatchProviderTests
{
    [Fact]
    public async Task ProcessCompletedBatches_UsesBatchProviderResults()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "demo-repo",
            LocalPath = null
        });

        var nodeId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "Demo.Project.OrderService",
            FilePath = "OrderService.cs"
        });

        var provider = new RecordingBatchProvider(nodeId);
        var registry = new SingleProviderRegistry(provider);
        var messageBus = new RecordingMessageBus();
        var service = new BatchAnalysisService(
            store,
            registry,
            messageBus,
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "lmstudio" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");
        await service.ProcessCompletedBatchesAsync("demo-repo");

        provider.SubmitCalls.ShouldBe(1);
        provider.StatusCalls.ShouldBe(1);
        provider.ResultsCalls.ShouldBe(1);
        (await store.GetPendingBatchesAsync("demo-repo")).ShouldBeEmpty();
        (await store.GetProjectAnalysesAsync("demo-repo")).Count.ShouldBe(1);
        messageBus.PublishedMessages.OfType<AnalysisBatchSubmitted>().Count().ShouldBe(1);
        messageBus.PublishedMessages.OfType<ProjectAnalysisResultsProcessed>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task SubmitAnalysisBatch_IncludesDefinesMethodChildren_InPrompt()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "demo-repo",
            LocalPath = null
        });

        var classId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "Demo.Project.OrderService",
            FilePath = "OrderService.cs"
        });

        var methodId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Method,
            Name = "ProcessOrder",
            QualifiedName = "Demo.Project.OrderService.ProcessOrder()",
            FilePath = "OrderService.cs",
            Properties = new()
            {
                ["signature"] = "ProcessOrder()",
                ["return_type"] = "Task",
                ["is_async"] = true
            }
        });

        await store.InsertEdgeAsync(new GraphEdge
        {
            Project = "demo-repo",
            SourceId = classId,
            TargetId = methodId,
            Type = EdgeType.DEFINES_METHOD
        });

        var provider = new RecordingBatchProvider(classId);
        var service = new BatchAnalysisService(
            store,
            new SingleProviderRegistry(provider),
            new RecordingMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "lmstudio" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");

        provider.LastSubmittedPrompt.ShouldNotBeNull();
        provider.LastSubmittedPrompt.ShouldContain("Methods:");
        provider.LastSubmittedPrompt.ShouldContain("async Task ProcessOrder()");
    }

    [Fact]
    public async Task SubmitAnalysisBatch_UsesAdminPromptOverride_ForSystemPrompt()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "demo-repo" });
        var nodeId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "Demo.Project.OrderService",
            FilePath = "OrderService.cs"
        });

        var provider = new RecordingBatchProvider(nodeId);
        var service = new BatchAnalysisService(
            store,
            new SingleProviderRegistry(provider),
            new RecordingMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "lmstudio" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance,
            new TestAgentPromptService(new Dictionary<string, string>
            {
                [AgentPromptCatalog.RepositoryAnalysisSystemPromptKey] = "custom repository analysis system prompt"
            }));

        await service.SubmitAnalysisBatchAsync("demo-repo");

        provider.LastSubmittedSystemPrompt.ShouldBe("custom repository analysis system prompt");
    }

    [Fact]
    public async Task SubmitAnalysisBatch_ForCRepo_PrefersOwnedFirmwareFunctions_OverVendoredComponents()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"codegraph-c-prompt-{Guid.NewGuid():N}");
        var firstPartyFile = Path.Combine(repoPath, "src/Display-Board/main/managers/display_manager.c");
        var vendoredFile = Path.Combine(repoPath, "src/Display-Board/components/espressif__esp_hosted/common/protobuf-c/protoc-c/c_message.cc");

        Directory.CreateDirectory(Path.GetDirectoryName(firstPartyFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(vendoredFile)!);

        try
        {
            await File.WriteAllTextAsync(firstPartyFile, """
                void display_manager_tick(void)
                {
                    // keep UI responsive
                }
                """);
            await File.WriteAllTextAsync(vendoredFile, """
                void protoc_codegen_pass(void)
                {
                    // vendored toolchain code
                }
                """);

            var store = new InMemoryGraphStore();
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "demo-repo",
                LocalPath = repoPath,
                Language = "C"
            });

            var firstPartyNodeId = await store.UpsertNodeAsync(new GraphNode
            {
                Project = "demo-repo",
                Label = NodeLabel.Function,
                Name = "display_manager_tick",
                QualifiedName = "demo-repo.display_manager_tick",
                FilePath = "src/Display-Board/main/managers/display_manager.c",
                StartLine = 1,
                EndLine = 4,
                Properties = new() { ["return_type"] = "void" }
            });

            await store.UpsertNodeAsync(new GraphNode
            {
                Project = "demo-repo",
                Label = NodeLabel.Function,
                Name = "protoc_codegen_pass",
                QualifiedName = "demo-repo.protoc_codegen_pass",
                FilePath = "src/Display-Board/components/espressif__esp_hosted/common/protobuf-c/protoc-c/c_message.cc",
                StartLine = 1,
                EndLine = 4,
                Properties = new() { ["return_type"] = "void" }
            });

            var provider = new RecordingBatchProvider(firstPartyNodeId);
            var service = new BatchAnalysisService(
                store,
                new SingleProviderRegistry(provider),
                new RecordingMessageBus(),
                new NoOpExclusionService(),
                Options.Create(new AnalysisOptions { DefaultProvider = "lmstudio", MaxSourceChars = 4000 }),
                new LocalFileSystem(),
                NullLogger<BatchAnalysisService>.Instance);

            await service.SubmitAnalysisBatchAsync("demo-repo", repoPath, includeAllSource: true);

            provider.LastSubmittedPrompt.ShouldNotBeNull();
            provider.LastSubmittedPrompt.ShouldContain("[Function] demo-repo.display_manager_tick");
            provider.LastSubmittedPrompt.ShouldContain("display_manager.c");
            provider.LastSubmittedPrompt.ShouldNotContain("demo-repo.protoc_codegen_pass");
            provider.LastSubmittedPrompt.ShouldNotContain("c_message.cc");
        }
        finally
        {
            if (Directory.Exists(repoPath))
                Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task SubmitAnalysisBatch_ForLmStudioProvider_LimitsObjectOrientedPromptNodeCount()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "demo-repo",
            LocalPath = null
        });

        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrdersController",
            QualifiedName = "Demo.Project.OrdersController",
            FilePath = "OrdersController.cs"
        });

        var serviceId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "Demo.Project.OrderService",
            FilePath = "OrderService.cs"
        });

        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "HelperUtility",
            QualifiedName = "Demo.Project.HelperUtility",
            FilePath = "HelperUtility.cs"
        });

        for (var i = 0; i < 5; i++)
        {
            var targetId = await store.UpsertNodeAsync(new GraphNode
            {
                Project = "demo-repo",
                DotnetProject = "Demo.Project",
                Label = NodeLabel.Interface,
                Name = $"Dependency{i}",
                QualifiedName = $"Demo.Project.Dependency{i}",
                FilePath = $"Dependency{i}.cs"
            });

            await store.InsertEdgeAsync(new GraphEdge
            {
                Project = "demo-repo",
                SourceId = serviceId,
                TargetId = targetId,
                Type = EdgeType.CALLS
            });
        }

        var provider = new RecordingBatchProvider(1, providerName: "lmstudio");
        var service = new BatchAnalysisService(
            store,
            new SingleProviderRegistry(provider),
            new RecordingMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions
            {
                DefaultProvider = "lmstudio",
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    MaxPromptNodes = 2,
                    MaxRelationshipTargetsPerType = 4,
                    MaxSourceChars = 16000
                }
            }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");

        provider.LastSubmittedPrompt.ShouldNotBeNull();
        provider.LastSubmittedPrompt.ShouldContain("Demo.Project.OrdersController");
        provider.LastSubmittedPrompt.ShouldContain("Demo.Project.OrderService");
        provider.LastSubmittedPrompt.ShouldNotContain("Demo.Project.HelperUtility");
        provider.LastSubmittedPrompt!.Split("[Class] ", StringSplitOptions.None).Length.ShouldBe(3);
    }

    [Fact]
    public async Task SubmitAnalysisBatch_UsesDbBackedAnalysisSettings_ForProviderModelAndTokenBudget()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "demo-repo" });
        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "demo-repo",
            DotnetProject = "Demo.Project",
            Label = NodeLabel.Class,
            Name = "OrderService",
            QualifiedName = "Demo.Project.OrderService",
            FilePath = "OrderService.cs"
        });

        var provider = new RecordingBatchProvider(1, providerName: "openai");
        var service = new BatchAnalysisService(
            store,
            new SingleProviderRegistry(provider),
            new RecordingMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "anthropic", Model = "claude-fallback", MaxTokensPerAnalysis = 100 }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance,
            analysisSettingsResolver: new FixedAnalysisSettingsResolver(new LlmAnalysisRuntimeConfig(
                "openai",
                "gpt-db",
                MaxTokensPerAnalysis: 1234,
                MaxTokensPerSynthesis: 5678,
                MaxFileSizeKb: 1024,
                MaxParallelAnalyses: 2,
                MaxSourceChars: 9000,
                UpdatedBy: null,
                UpdatedAtUtc: null,
                HasDbConfig: true)));

        await service.SubmitAnalysisBatchAsync("demo-repo");

        provider.LastBatchRequest.ShouldNotBeNull();
        provider.LastBatchRequest.Model.ShouldBe("gpt-db");
        provider.LastBatchRequest.MaxTokens.ShouldBe(1234);
    }

    [Fact]
    public async Task SynthesizeRepoSummary_UsesOnlyProjectsFromCompletedBatch()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "SceneWorks" });

        await store.UpsertProjectAnalysisAsync("SceneWorks", new StoredProjectAnalysis(
            Repo: "SceneWorks",
            ProjectName: "legacy-api",
            Summary: "SceneWorks is a FastAPI-based web API.",
            Confidence: ConfidenceLevel.High,
            Endpoints: [],
            Services: [],
            ExternalDependencies: [],
            DatabaseTables: [],
            ModelUsed: "old-model",
            UpdatedAt: DateTime.UtcNow.AddDays(-10)));

        await store.UpsertProjectAnalysisAsync("SceneWorks", new StoredProjectAnalysis(
            Repo: "SceneWorks",
            ProjectName: "desktop-app",
            Summary: "SceneWorks is a Rust and Tauri desktop application.",
            Confidence: ConfidenceLevel.High,
            Endpoints: [],
            Services: [],
            ExternalDependencies: [],
            DatabaseTables: [],
            ModelUsed: "new-model",
            UpdatedAt: DateTime.UtcNow));

        var batchRecordId = await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
        {
            Repo = "SceneWorks",
            ProviderBatchId = "batch_current",
            ProviderName = "lmstudio",
            ExecutionMode = "native_batch",
            IncludeAllSource = false,
            Status = "completed",
            RequestCount = 1,
            CompletedCount = 1,
            SubmittedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });

        await store.CreateBatchRequestsAsync([
            new AnalysisBatchRequestEntity
            {
                BatchId = batchRecordId,
                Sequence = 0,
                CustomId = "proj_SceneWorks_desktop-app",
                NodeLabel = "desktop-app",
                RequestPayloadJson = "{}",
                Status = "succeeded",
                CompletedAt = DateTime.UtcNow
            }
        ]);

        var provider = new RecordingSynthesisProvider();
        var service = new BatchAnalysisService(
            store,
            new SingleProviderRegistry(provider),
            new RecordingMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "lmstudio" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SynthesizeRepoSummaryAsync("SceneWorks", "batch_current", CancellationToken.None);

        provider.LastSynthesisPrompt.ShouldNotBeNull();
        provider.LastSynthesisPrompt.ShouldContain("Rust and Tauri desktop application");
        provider.LastSynthesisPrompt.ShouldNotContain("FastAPI-based web API");

        var summary = await store.GetRepositorySummaryAsync("SceneWorks");
        summary.ShouldNotBeNull();
        summary.Summary.ShouldBe("Fresh SceneWorks summary.");
        summary.SourceHash.ShouldBe("batch_current");
    }

    private sealed class RecordingBatchProvider(long nodeId, string providerName = "lmstudio") : IAnalysisModelProvider
    {
        public int SubmitCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int ResultsCalls { get; private set; }
        public string? LastSubmittedPrompt { get; private set; }
        public string? LastSubmittedSystemPrompt { get; private set; }
        public AnalysisRequestOptions? LastBatchRequest { get; private set; }

        public string ProviderName => providerName;

        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
            IReadOnlyList<AnalysisBatchRequestItem> items,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            SubmitCalls++;
            LastSubmittedPrompt = items.Single().Prompt.UserPrompt;
            LastSubmittedSystemPrompt = items.Single().Prompt.SystemPrompt;
            LastBatchRequest = request;
            return Task.FromResult(new AnalysisBatchSubmissionResult("batch_1", "submitted"));
        }

        public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
        {
            StatusCalls++;
            return Task.FromResult(new AnalysisBatchStatusResult(batchId, "completed", IsCompleted: true));
        }

        public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
            string batchId,
            IReadOnlyList<string>? requestIds = null,
            CancellationToken ct = default)
        {
            ResultsCalls++;
            var customId = requestIds?.Single() ?? "req_1";
            var json =
                $$"""
                  {
                    "projectSummary": "Handles order work for the demo project.",
                    "confidence": "high",
                    "nodes": [
                      { "nodeId": {{nodeId}}, "description": "Coordinates order operations.", "confidence": "high" }
                    ]
                  }
                  """;
            return Task.FromResult<IReadOnlyList<AnalysisBatchItemResult>>([
                new AnalysisBatchItemResult(customId, "succeeded", json, "qwen3")
            ]);
        }
    }

    private sealed class RecordingSynthesisProvider : IAnalysisModelProvider
    {
        public string? LastSynthesisPrompt { get; private set; }

        public string ProviderName => "lmstudio";

        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            LastSynthesisPrompt = prompt.UserPrompt;
            return Task.FromResult(new AnalysisTextResponse(
                """{ "repoSummary": "Fresh SceneWorks summary.", "confidence": "high" }""",
                "qwen3",
                ProviderName));
        }

        public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
            IReadOnlyList<AnalysisBatchRequestItem> items,
            AnalysisRequestOptions request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
            string batchId,
            IReadOnlyList<string>? requestIds = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleProviderRegistry(IAnalysisModelProvider provider) : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => provider;
    }

    private sealed class FixedAnalysisSettingsResolver(
        LlmAnalysisRuntimeConfig config) : IDbBackedAnalysisSettingsResolver
    {
        public Task<LlmAnalysisRuntimeConfig> GetAnalysisAsync(CancellationToken ct = default) =>
            Task.FromResult(config);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> PublishedMessages { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            PublishedMessages.Add(message!);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpExclusionService : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) => Task.FromResult<string?>(null);
        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) => Task.FromResult(new HashSet<string>());
        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() => Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);
        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) => throw new NotSupportedException();
        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();
        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) => Task.CompletedTask;
    }
}
