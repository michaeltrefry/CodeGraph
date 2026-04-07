using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisServiceDirectFallbackTests
{
    [Fact]
    public async Task ProcessCompletedBatches_UsesDirectFallback_ForNonBatchProviders()
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

        var provider = new RecordingDirectOnlyProvider(nodeId);
        var registry = new SingleProviderRegistry(provider);
        var messageBus = new RecordingMessageBus();
        var service = new BatchAnalysisService(
            store,
            registry,
            messageBus,
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "local" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");

        provider.ExecuteCalls.ShouldBe(0);

        await service.ProcessCompletedBatchesAsync("demo-repo");

        provider.ExecuteCalls.ShouldBe(1);
        (await store.GetPendingBatchesAsync("demo-repo")).ShouldBeEmpty();
        (await store.GetProjectAnalysesAsync("demo-repo")).Count.ShouldBe(1);
        messageBus.PublishedMessages.OfType<AnalysisBatchSubmitted>().Count().ShouldBe(1);
        messageBus.PublishedMessages.OfType<ProjectAnalysisResultsProcessed>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task ProcessCompletedBatches_IncludesDefinesMethodChildren_InPrompt()
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

        var provider = new RecordingDirectOnlyProvider(classId);
        var registry = new SingleProviderRegistry(provider);
        var messageBus = new RecordingMessageBus();
        var service = new BatchAnalysisService(
            store,
            registry,
            messageBus,
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions { DefaultProvider = "local" }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");
        await service.ProcessCompletedBatchesAsync("demo-repo");

        provider.LastPrompt.ShouldNotBeNull();
        provider.LastPrompt.ShouldContain("Methods:");
        provider.LastPrompt.ShouldContain("async Task ProcessOrder()");
    }

    [Fact]
    public async Task ProcessCompletedBatches_ForCRepo_PrefersOwnedFirmwareFunctions_OverVendoredComponents()
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

            var provider = new RecordingDirectOnlyProvider(firstPartyNodeId);
            var registry = new SingleProviderRegistry(provider);
            var messageBus = new RecordingMessageBus();
            var service = new BatchAnalysisService(
                store,
                registry,
                messageBus,
                new NoOpExclusionService(),
                Options.Create(new AnalysisOptions { DefaultProvider = "local", MaxSourceChars = 4000 }),
                new LocalFileSystem(),
                NullLogger<BatchAnalysisService>.Instance);

            await service.SubmitAnalysisBatchAsync("demo-repo", repoPath, includeAllSource: true);
            await service.ProcessCompletedBatchesAsync("demo-repo");

            provider.LastPrompt.ShouldNotBeNull();
            provider.LastPrompt.ShouldContain("[Function] demo-repo.display_manager_tick");
            provider.LastPrompt.ShouldContain("display_manager.c");
            provider.LastPrompt.ShouldNotContain("demo-repo.protoc_codegen_pass");
            provider.LastPrompt.ShouldNotContain("c_message.cc");
        }
        finally
        {
            if (Directory.Exists(repoPath))
                Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessCompletedBatches_RetriesTransientDirectFallbackFailures_OnLaterPass()
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

        var provider = new FlakyDirectOnlyProvider(nodeId);
        var registry = new SingleProviderRegistry(provider);
        var messageBus = new RecordingMessageBus();
        var service = new BatchAnalysisService(
            store,
            registry,
            messageBus,
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions
            {
                DefaultProvider = "local",
                Local = new LocalAnalysisProviderOptions
                {
                    DirectFallbackMaxAttempts = 3
                }
            }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");

        await service.ProcessCompletedBatchesAsync("demo-repo");

        provider.ExecuteCalls.ShouldBe(1);
        (await store.GetPendingBatchesAsync("demo-repo")).Count.ShouldBe(1);
        (await store.GetBatchRequestsAsync(1)).Single().Status.ShouldBe("pending");
        (await store.GetBatchRequestsAsync(1)).Single().AttemptCount.ShouldBe(1);
        messageBus.PublishedMessages.OfType<ProjectAnalysisResultsProcessed>().ShouldBeEmpty();

        await service.ProcessCompletedBatchesAsync("demo-repo");

        provider.ExecuteCalls.ShouldBe(2);
        (await store.GetPendingBatchesAsync("demo-repo")).ShouldBeEmpty();
        (await store.GetProjectAnalysesAsync("demo-repo")).Count.ShouldBe(1);
        messageBus.PublishedMessages.OfType<ProjectAnalysisResultsProcessed>().Count().ShouldBe(1);
    }

    private sealed class RecordingDirectOnlyProvider(long nodeId) : IAnalysisModelProvider
    {
        public int ExecuteCalls { get; private set; }
        public string? LastPrompt { get; private set; }

        public string ProviderName => "local";

        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            ExecuteCalls++;
            LastPrompt = prompt.UserPrompt;
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
            return Task.FromResult(new AnalysisTextResponse(json, "qwen3", ProviderName));
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

    private sealed class FlakyDirectOnlyProvider(long nodeId) : IAnalysisModelProvider
    {
        public int ExecuteCalls { get; private set; }
        public string ProviderName => "local";

        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            ExecuteCalls++;
            if (ExecuteCalls == 1)
                throw new RetryableAnalysisException("LM Studio queue timeout");

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
            return Task.FromResult(new AnalysisTextResponse(json, "qwen3", ProviderName));
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

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> PublishedMessages { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            PublishedMessages.Add(message);
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
