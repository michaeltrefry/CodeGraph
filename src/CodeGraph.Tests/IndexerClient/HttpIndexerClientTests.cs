using System.Net;
using System.Text.Json;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.IndexerClient;

public class HttpIndexerClientTests
{
    [Fact]
    public void AddCodeGraphIndexerClient_DisablesHiddenTransportTimeout()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:Indexer:BaseUrl"] = "http://indexer.local"
            })
            .Build();
        services.AddCodeGraphIndexerClient(configuration);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(IndexerClientOptions.DefaultHttpClientName);

        client.BaseAddress.ShouldBe(new Uri("http://indexer.local/"));
        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task StartReAnalyzeRepositoryAsync_PostsRequestAndReturnsAcceptedRun()
    {
        var accepted = new IndexerAcceptedResponse("queued", "Queued.", 42, "/api/indexer/runs/42");
        var handler = new RecordingHandler(accepted);
        var client = CreateClient(handler);

        var response = await client.StartReAnalyzeRepositoryAsync("Michael", " SceneWorks ", "reanalyze-42");

        response.ShouldBe(accepted);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://indexer.local/api/indexer/repositories/reanalyze");
        request.Headers.Contains(CodeGraphInternalServiceAuthenticationDefaults.HeaderName).ShouldBeTrue();
        request.Headers.GetValues("Idempotency-Key").Single().ShouldBe("reanalyze-42");
        var body = await request.Content!.ReadAsStringAsync();
        body.ShouldBe("{\"repo\":\"SceneWorks\"}");
    }

    [Fact]
    public async Task StartProcessRepositoriesAsync_PostsRequestWithInternalIdentityHeader()
    {
        var handler = new RecordingHandler(new IndexerAcceptedResponse("queued", "ok", 42, "/api/indexer/runs/42"));
        var client = CreateClient(handler);

        var response = await client.StartProcessRepositoriesAsync(
            "Michael",
            new ProcessRequest { Repos = ["CodeGraph"], IncludeAllSource = true },
            "submission-42");

        response.RunId.ShouldBe(42);
        handler.Requests.Count.ShouldBe(1);
        var request = handler.Requests[0];
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://indexer.local/api/indexer/repositories/process");
        request.Headers.Contains(CodeGraphInternalServiceAuthenticationDefaults.HeaderName).ShouldBeTrue();
        request.Headers.GetValues("Idempotency-Key").Single().ShouldBe("submission-42");
        var body = await request.Content!.ReadAsStringAsync();
        body.ShouldContain("\"repos\":[\"CodeGraph\"]");
        body.ShouldContain("\"includeAllSource\":true");
    }

    [Fact]
    public async Task ListRunsAsync_SendsFiltersInQueryString()
    {
        var run = new IndexerRunResponse(7, "link", "completed", "michael", "all", null, null, null, DateTime.UtcNow, null, DateTime.UtcNow);
        var handler = new RecordingHandler(new[] { run });
        var client = CreateClient(handler);

        var runs = await client.ListRunsAsync("michael", status: "completed", operation: "link", take: 500);

        runs.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe("/api/indexer/runs?status=completed&operation=link&take=200");
    }

    [Fact]
    public async Task GetRunAsync_ReturnsNullForNotFound()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, null);
        var client = CreateClient(handler);

        var run = await client.GetRunAsync("michael", 404);

        run.ShouldBeNull();
    }

    [Fact]
    public async Task CancelRunAsync_PostsToDurableCancellationEndpoint()
    {
        var run = new IndexerRunResponse(
            7,
            "link",
            "running",
            "michael",
            "all",
            null,
            null,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            AttemptCount: 1,
            CancelRequestedAt: DateTime.UtcNow,
            RetrySafe: true);
        var handler = new RecordingHandler(run);
        var client = CreateClient(handler);

        var canceled = await client.CancelRunAsync("michael", 7);

        canceled!.CancelRequestedAt.ShouldNotBeNull();
        handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe("/api/indexer/runs/7/cancel");
        handler.Requests[0].Headers.Contains(CodeGraphInternalServiceAuthenticationDefaults.HeaderName).ShouldBeTrue();
    }

    [Fact]
    public async Task Client_ThrowsTypedExceptionForErrorResponse()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            new { error = "invalid_request", message = "Nope." });
        var client = CreateClient(handler);

        var ex = await Should.ThrowAsync<IndexerClientException>(() => client.StartLinkAsync("michael"));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.ErrorCode.ShouldBe("invalid_request");
        ex.Message.ShouldBe("Nope.");
    }

    [Fact]
    public async Task Client_DoesNotForwardUnstructuredServerErrorBody()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            "<html>developer exception stack</html>",
            "text/html");
        var client = CreateClient(handler);

        var ex = await Should.ThrowAsync<IndexerClientException>(() => client.StartLinkAsync("michael"));

        ex.ErrorCode.ShouldBeNull();
        ex.Message.ShouldBe("Indexer request failed with status code 500.");
        ex.Message.ShouldNotContain("developer exception");
    }

    private static HttpIndexerClient CreateClient(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://indexer.local/")
        };
        var factory = new StubHttpClientFactory(httpClient);
        var options = new IndexerClientOptions { Audience = "codegraph-indexer" };
        var tokenFactory = new InternalServiceTokenFactory(Options.Create(new InternalServiceAuthOptions
        {
            HmacKey = "test-key-with-enough-entropy"
        }));

        return new HttpIndexerClient(factory, Options.Create(options), tokenFactory);
    }

    private static AnalysisBatchResponse CreateBatch(string repo) => new(
        11,
        repo,
        "batch-11",
        "anthropic",
        "batch",
        true,
        "pending",
        2,
        0,
        DateTime.UtcNow,
        null);

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly HttpStatusCode _statusCode;
        private readonly object? _response;
        private readonly string? _rawContentType;

        public RecordingHandler(object response)
            : this(HttpStatusCode.OK, response)
        {
        }

        public RecordingHandler(HttpStatusCode statusCode, object? response)
        {
            _statusCode = statusCode;
            _response = response;
        }

        public RecordingHandler(HttpStatusCode statusCode, string response, string contentType)
            : this(statusCode, response)
        {
            _rawContentType = contentType;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequestAsync(request, cancellationToken).GetAwaiter().GetResult());
            var message = new HttpResponseMessage(_statusCode);
            if (_response is not null)
            {
                message.Content = new StringContent(
                    _rawContentType is null ? JsonSerializer.Serialize(_response, JsonOptions) : (string)_response,
                    System.Text.Encoding.UTF8,
                    _rawContentType ?? "application/json");
            }

            return Task.FromResult(message);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                clone.Content = new StringContent(body, System.Text.Encoding.UTF8, request.Content.Headers.ContentType?.MediaType);
            }

            return clone;
        }
    }
}
