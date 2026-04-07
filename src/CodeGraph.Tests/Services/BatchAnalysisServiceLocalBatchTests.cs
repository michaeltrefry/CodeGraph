using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using System.Net;
using System.Text;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisServiceLocalBatchTests
{
    [Fact]
    public async Task SubmitAndProcessBatch_UsesStoredRequestPayloadJson_ForLocalProvider()
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

        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "model": "local-model",
                          "choices": [
                            {
                              "message": {
                                "content": "{\"projectSummary\":\"Handles orders\",\"confidence\":\"high\",\"nodes\":[{\"nodeId\":{{nodeId}},\"description\":\"Coordinates order operations.\",\"confidence\":\"high\"}]}"
                              }
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                }))),
            store,
            Options.Create(new AnalysisOptions
            {
                DefaultProvider = "local",
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model"
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        var registry = new AnalysisProviderRegistry([provider], Options.Create(new AnalysisOptions { DefaultProvider = "local" }));
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
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model"
                }
            }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.SubmitAnalysisBatchAsync("demo-repo");

        var batch = await store.GetLatestBatchAsync("demo-repo");
        batch.ShouldNotBeNull();
        batch.ExecutionMode.ShouldBe("native_batch");

        var storedRequest = (await store.GetBatchRequestsAsync(batch.Id)).Single();
        storedRequest.RequestPayloadJson.ShouldNotBeNullOrWhiteSpace();
        storedRequest.Status.ShouldBe("pending");

        await service.ProcessCompletedBatchesAsync("demo-repo");

        (await store.GetPendingBatchesAsync("demo-repo")).ShouldBeEmpty();
        (await store.GetProjectAnalysesAsync("demo-repo")).Count.ShouldBe(1);
        (await store.GetNodeAnalysisAsync(nodeId)).ShouldNotBeNull();
        messageBus.PublishedMessages.OfType<AnalysisBatchSubmitted>().Count().ShouldBe(1);
        messageBus.PublishedMessages.OfType<ProjectAnalysisResultsProcessed>().Count().ShouldBe(1);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "") => client;
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
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
