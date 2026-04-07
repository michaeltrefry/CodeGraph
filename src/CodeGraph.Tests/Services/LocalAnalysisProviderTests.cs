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

namespace CodeGraph.Tests.Services;

public class LocalAnalysisProviderTests
{
    [Fact]
    public void NormalizeBaseUrl_RewritesLocalhost_WhenRunningInContainer()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");

        try
        {
            var normalized = LocalAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

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
            var normalized = LocalAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

            normalized.ShouldBe("http://localhost:1234/v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previous);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsHelpfulError_WhenLocalModelIsMissing()
    {
        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = ""
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        var act = () => provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("CodeGraph:AnalysisOptions:Local:Model");
    }

    [Fact]
    public void SerializeRequestBodyForTests_OmitsNullResponseFormat()
    {
        var json = LocalAnalysisProvider.SerializeRequestBodyForTests(
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
        var json = LocalAnalysisProvider.SerializeRequestBodyForTests(
            "google/gemma-4-26b-a4b",
            256,
            true,
            new AnalysisPrompt("system", "user"));

        json.ShouldContain("\"response_format\":{\"type\":\"json_object\"}");
    }

    [Fact]
    public async Task ExecuteAsync_RetriesWithoutJsonObjectResponseFormat_WhenLocalServerRejectsStructuredOutput()
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
                      "model": "local-model",
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

        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(handler)),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model",
                    UseJsonObjectResponseFormat = true
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

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
    public async Task ExecuteAsync_AppliesConfiguredHttpTimeout()
    {
        var client = new HttpClient(new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "model": "local-model",
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

        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(client),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model",
                    TimeoutSeconds = 300
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        await provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task ExecuteAsync_WrapsTransientTimeouts_AsRetryableAnalysisException()
    {
        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")))),
            new InMemoryGraphStore(),
            Options.Create(new AnalysisOptions
            {
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model",
                    TimeoutSeconds = 300
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        var act = () => provider.ExecuteAsync(
            new AnalysisPrompt("system", "user"),
            new AnalysisRequestOptions(),
            CancellationToken.None);

        await Should.ThrowAsync<RetryableAnalysisException>(act);
    }

    [Fact]
    public async Task GetBatchStatusAsync_ProcessesStoredRequest_AndReturnsCompletedResults()
    {
        var store = new InMemoryGraphStore();
        var batch = new AnalysisBatchEntity
        {
            Repo = "demo-repo",
            ProviderBatchId = "local_batch_1",
            ProviderName = "local",
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

        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "model": "local-model",
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
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model"
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        var status = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        var results = await provider.GetBatchResultsAsync(batch.ProviderBatchId, ["req_1"], CancellationToken.None);

        status.IsCompleted.ShouldBeTrue();
        results.Count.ShouldBe(1);
        results[0].Status.ShouldBe("succeeded");
        results[0].Text.ShouldNotBeNull();
        results[0].Text!.ShouldContain("\"projectSummary\":\"Firmware\"");

        var storedRequest = (await store.GetBatchRequestsAsync(batchId)).Single();
        storedRequest.Status.ShouldBe("succeeded");
        storedRequest.ResponseText.ShouldNotBeNull();
        storedRequest.ResponseText!.ShouldContain("\"projectSummary\":\"Firmware\"");
        storedRequest.ModelUsed.ShouldBe("local-model");
    }

    [Fact]
    public async Task GetBatchStatusAsync_KeepsRetryableFailurePending_ForALaterPoll()
    {
        var store = new InMemoryGraphStore();
        var batch = new AnalysisBatchEntity
        {
            Repo = "demo-repo",
            ProviderBatchId = "local_batch_retry",
            ProviderName = "local",
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

        var provider = new LocalAnalysisProvider(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")))),
            store,
            Options.Create(new AnalysisOptions
            {
                Local = new LocalAnalysisProviderOptions
                {
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "local-model",
                    DirectFallbackMaxAttempts = 3
                }
            }),
            NullLogger<LocalAnalysisProvider>.Instance);

        var firstStatus = await provider.GetBatchStatusAsync(batch.ProviderBatchId, CancellationToken.None);
        var afterFirstAttempt = (await store.GetBatchRequestsAsync(batchId)).Single();
        firstStatus.IsCompleted.ShouldBeFalse();
        afterFirstAttempt.Status.ShouldBe("pending");
        afterFirstAttempt.AttemptCount.ShouldBe(1);
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return _responses.Dequeue();
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

}
