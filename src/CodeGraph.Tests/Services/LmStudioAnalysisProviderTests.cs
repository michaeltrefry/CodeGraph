using Shouldly;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Tests.Services;

public class LmStudioAnalysisProviderTests
{
    [Fact]
    public void NormalizeBaseUrl_RewritesLocalhost_WhenRunningInContainer()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");

        try
        {
            var normalized = LmStudioAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

            normalized.ShouldBe("http://host.docker.internal:1234/v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previous);
        }
    }

    [Fact]
    public void NormalizeBaseUrl_LeavesLocalhostAlone_WhenNotRunningInContainer()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "false");

        try
        {
            var normalized = LmStudioAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

            normalized.ShouldBe("http://localhost:1234/v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previous);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsHelpfulError_WhenLmStudioModelIsMissing()
    {
        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = ""
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var act = () => provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("CodeGraph:AnalysisOptions:LmStudio:Model");
    }

    [Fact]
    public void SerializeRequestBodyForTests_OmitsNullResponseFormat()
    {
        var json = LmStudioAnalysisProvider.SerializeRequestBodyForTests(
            "google/gemma-4-26b-a4b",
            256,
            false,
            new AnalysisPrompt("system", "user"));

        json.ShouldNotContain("response_format");
        json.ShouldContain("\"max_tokens\":256");
    }

    [Fact]
    public void SerializeRequestBodyForTests_IncludesResponseFormat_WhenEnabled()
    {
        var json = LmStudioAnalysisProvider.SerializeRequestBodyForTests(
            "google/gemma-4-26b-a4b",
            256,
            true,
            new AnalysisPrompt("system", "user"));

        json.ShouldContain("\"response_format\":{\"type\":\"json_object\"}");
    }

    [Fact]
    public async Task ExecuteAsync_RetriesWithoutJsonObjectResponseFormat_WhenLmStudioServerRejectsStructuredOutput()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"Failed to parse input at pos 0: <|channel>thought\"}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "model": "lmstudio-model",
                      "choices": [
                        {
                          "message": {
                            "content": "<|channel>thought\n<channel|>{\"projectSummary\":\"Firmware\",\"confidence\":\"high\",\"nodes\":[]}"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(handler)),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    UseJsonObjectResponseFormat = true
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var response = await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        response.Text.ShouldContain("\"projectSummary\":\"Firmware\"");
        handler.RequestBodies.Count.ShouldBe(2);

        using var firstDoc = JsonDocument.Parse(handler.RequestBodies[0]);
        firstDoc.RootElement.TryGetProperty("response_format", out _).ShouldBeTrue();

        using var secondDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        secondDoc.RootElement.TryGetProperty("response_format", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_RecoversJsonFromBadRequestParserError_WhenServerIncludesModelOutput()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """
                    {"error":"Failed to parse input at pos 0: <|channel>thought\n<channel|>{\"projectSummary\":\"Recovered\",\"confidence\":\"high\",\"nodes\":[]}"}
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(handler)),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    UseJsonObjectResponseFormat = false
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var response = await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        response.Text.ShouldContain("\"projectSummary\":\"Recovered\"");
        response.ModelUsed.ShouldBe("lmstudio-model");
        handler.RequestBodies.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_AppliesConfiguredHttpTimeout()
    {
        var client = new HttpClient(new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "model": "lmstudio-model",
                      "choices": [
                        {
                          "message": {
                            "content": "{\"projectSummary\":\"ok\"}"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            }));

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(client),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    TimeoutSeconds = 300
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task ExecuteAsync_UsesDbBackedProviderConfig_WhenResolverSuppliesOverride()
    {
        var handler = new SequencedHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "model": "db-model",
                  "choices": [
                    {
                      "message": {
                        "content": "{\"projectSummary\":\"ok\"}"
                      }
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(handler)),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    ApiKey = "fallback-token",
                    BaseUrl = "http://fallback.example/v1",
                    Model = "fallback-model"
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance,
            providerConfigResolver: new StaticProviderConfigResolver(new LlmProviderRuntimeConfig(
                "lmstudio",
                ApiKey: "db-token",
                EndpointUrl: "http://db.example/v1",
                ApiVersion: null,
                Model: "db-model",
                Models: ["db-model"],
                HasDbConfig: true,
                HasDbToken: true)));

        await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        handler.RequestUris.Single().ShouldBe("http://db.example/v1/chat/completions");
        handler.AuthorizationHeaders.Single().ShouldBe("Bearer db-token");
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("model").GetString().ShouldBe("db-model");
    }

    [Fact]
    public async Task ExecuteAsync_ReadsChoiceText_WhenMessageContentIsMissing()
    {
        var client = new HttpClient(new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "model": "lmstudio-model",
                      "choices": [
                        {
                          "text": "{\"projectSummary\":\"ok\",\"confidence\":\"high\",\"nodes\":[]}"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            }));

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(client),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model"
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var response = await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        response.Text.ShouldContain("\"projectSummary\":\"ok\"");
    }

    [Fact]
    public async Task ExecuteAsync_ReadsNestedTextParts_FromArrayContent()
    {
        var client = new HttpClient(new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "model": "lmstudio-model",
                      "choices": [
                        {
                          "message": {
                            "content": [
                              { "type": "text", "text": "{\"projectSummary\":\"nested\",\"confidence\":\"medium\",\"nodes\":[]}" }
                            ]
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            }));

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(client),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model"
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var response = await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        response.Text.ShouldContain("\"projectSummary\":\"nested\"");
    }

    [Fact]
    public async Task ExecuteAsync_WrapsTransientTimeouts_AsRetryableAnalysisException()
    {
        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")))),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    TimeoutSeconds = 300
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var act = () => provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        await Should.ThrowAsync<RetryableAnalysisException>(act);
    }

    [Fact]
    public async Task GetBatchStatusAsync_StartsBackgroundProcessing_AndLaterReturnsCompletedResults()
    {
        var store = new InMemoryGraphStore();
        var batch = new AnalysisBatchEntity
        {
            Repo = "demo-repo",
            ProviderBatchId = "local_batch_1",
            ProviderName = "lmstudio",
            ExecutionMode = "native_batch",
            Status = "submitted",
            RequestCount = 1,
            SubmittedAt = DateTime.UtcNow
        };
        var batchId = await store.CreateAnalysisBatchAsync(batch);

        var payload = new AnalysisBatchRequestPayload(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(MaxTokens: 256));

        await store.CreateBatchRequestsAsync([
            new AnalysisBatchRequestEntity
            {
                BatchId = batchId,
                Sequence = 0,
                CustomId = "req_1",
                NodeLabel = "Demo.Project",
                RequestPayloadJson = JsonSerializer.Serialize(payload, CodeGraphJsonDefaults.CamelCase),
                Status = "pending"
            }
        ]);

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "model": "lmstudio-model",
                          "choices": [
                            {
                              "message": {
                                "content": "{\"projectSummary\":\"Firmware\",\"confidence\":\"high\",\"nodes\":[]}"
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
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model"
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var firstStatus = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        for (var attempt = 0;
             attempt < 20 && firstStatus.ProcessingStatus is "submitted";
             attempt++)
        {
            await Task.Delay(25);
            firstStatus = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        }

        firstStatus.ProcessingStatus.ShouldBeOneOf("processing", "completed");

        AnalysisBatchRequestEntity storedRequest = (await store.GetBatchRequestsAsync(batchId)).Single();
        for (var attempt = 0; attempt < 20 && !string.Equals(storedRequest.Status, "succeeded", StringComparison.OrdinalIgnoreCase); attempt++)
        {
            await Task.Delay(25);
            storedRequest = (await store.GetBatchRequestsAsync(batchId)).Single();
        }

        var secondStatus = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        var results = await provider.GetBatchResultsAsync(batch.ProviderBatchId, ["req_1"], CancellationToken.None);

        secondStatus.IsCompleted.ShouldBeTrue();
        results.Count.ShouldBe(1);
        results[0].Status.ShouldBe("succeeded");
        results[0].Text.ShouldNotBeNull();
        results[0].Text!.ShouldContain("\"projectSummary\":\"Firmware\"");

        storedRequest.Status.ShouldBe("succeeded");
        storedRequest.ResponseText.ShouldNotBeNull();
        storedRequest.ResponseText!.ShouldContain("\"projectSummary\":\"Firmware\"");
        storedRequest.ModelUsed.ShouldBe("lmstudio-model");
    }

    [Fact]
    public async Task GetBatchStatusAsync_KeepsRetryableFailurePending_ForALaterPoll()
    {
        var store = new InMemoryGraphStore();
        var batch = new AnalysisBatchEntity
        {
            Repo = "demo-repo",
            ProviderBatchId = "local_batch_retry",
            ProviderName = "lmstudio",
            ExecutionMode = "native_batch",
            Status = "submitted",
            RequestCount = 1,
            SubmittedAt = DateTime.UtcNow
        };
        var batchId = await store.CreateAnalysisBatchAsync(batch);

        var payload = new AnalysisBatchRequestPayload(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions());

        await store.CreateBatchRequestsAsync([
            new AnalysisBatchRequestEntity
            {
                BatchId = batchId,
                Sequence = 0,
                CustomId = "req_retry",
                NodeLabel = "Demo.Project",
                RequestPayloadJson = JsonSerializer.Serialize(payload, CodeGraphJsonDefaults.CamelCase),
                Status = "pending"
            }
        ]);

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")))),
            store,
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    DirectFallbackMaxAttempts = 3
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance);

        var firstStatus = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        firstStatus.IsCompleted.ShouldBeFalse();

        AnalysisBatchRequestEntity afterFirstAttempt = (await store.GetBatchRequestsAsync(batchId)).Single();
        for (var attempt = 0; attempt < 20 && afterFirstAttempt.AttemptCount == 0; attempt++)
        {
            await Task.Delay(25);
            afterFirstAttempt = (await store.GetBatchRequestsAsync(batchId)).Single();
        }

        afterFirstAttempt.Status.ShouldBe("pending");
        afterFirstAttempt.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetBatchStatusAsync_UsesScopedStoreForBackgroundBatchRunner()
    {
        const string providerBatchId = "local_batch_scoped";
        var rootStore = new InMemoryGraphStore();
        var scopedStore = new InMemoryGraphStore();
        var rootBatchId = await SeedPendingLmStudioBatchAsync(rootStore, providerBatchId, "req_root");
        var scopedBatchId = await SeedPendingLmStudioBatchAsync(scopedStore, providerBatchId, "req_scoped");

        var provider = new LmStudioAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")))),
            rootStore,
            Options.Create(new AnalysisOptions
            {
                LmStudio = new LmStudioAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "lmstudio-model",
                    DirectFallbackMaxAttempts = 3
                }
            }),
            NullLogger<LmStudioAnalysisProvider>.Instance,
            new SingleStoreScopeFactory(scopedStore));

        await provider.GetBatchStatusAsync(providerBatchId, CancellationToken.None);

        AnalysisBatchRequestEntity scopedRequest = (await scopedStore.GetBatchRequestsAsync(scopedBatchId)).Single();
        for (var attempt = 0; attempt < 20 && scopedRequest.AttemptCount == 0; attempt++)
        {
            await Task.Delay(25);
            scopedRequest = (await scopedStore.GetBatchRequestsAsync(scopedBatchId)).Single();
        }

        scopedRequest.AttemptCount.ShouldBe(1);
        var rootRequest = (await rootStore.GetBatchRequestsAsync(rootBatchId)).Single();
        rootRequest.AttemptCount.ShouldBe(0);
    }

    private static async Task<long> SeedPendingLmStudioBatchAsync(InMemoryGraphStore store, string providerBatchId, string customId)
    {
        var batchId = await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
        {
            Repo = "demo-repo",
            ProviderBatchId = providerBatchId,
            ProviderName = "lmstudio",
            ExecutionMode = "native_batch",
            Status = "submitted",
            RequestCount = 1,
            SubmittedAt = DateTime.UtcNow
        });

        var payload = new AnalysisBatchRequestPayload(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions());

        await store.CreateBatchRequestsAsync([
            new AnalysisBatchRequestEntity
            {
                BatchId = batchId,
                Sequence = 0,
                CustomId = customId,
                NodeLabel = "Demo.Project",
                RequestPayloadJson = JsonSerializer.Serialize(payload, CodeGraphJsonDefaults.CamelCase),
                Status = "pending"
            }
        ]);

        return batchId;
    }

    private sealed class StubHttpClientFactory(HttpClient? client = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "")
        {
            return client ?? throw new NotSupportedException("HTTP should not be reached in this test.");
        }
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];
        public List<string> RequestUris { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? "");
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return _responses.Dequeue();
        }
    }

    private sealed class StaticProviderConfigResolver(LlmProviderRuntimeConfig config) : IDbBackedLlmProviderConfigResolver
    {
        public Task<LlmProviderRuntimeConfig> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(config);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class SingleStoreScopeFactory(IGraphStore store) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            return new SingleStoreScope(store);
        }
    }

    private sealed class SingleStoreScope : IServiceScope
    {
        private readonly ServiceProvider _serviceProvider;

        public SingleStoreScope(IGraphStore store)
        {
            _serviceProvider = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton<IGraphStore>(store)
                .BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider => _serviceProvider;

        public void Dispose()
        {
            _serviceProvider.Dispose();
        }
    }

}
