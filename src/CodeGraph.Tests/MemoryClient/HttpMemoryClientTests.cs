using System.Net;
using System.Text.Json;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Memory.Client;
using CodeGraph.Models.Memory;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.MemoryClient;

public class HttpMemoryClientTests
{
    [Fact]
    public async Task QueueClaimsAsync_PostsRequestWithInternalIdentityHeader()
    {
        var handler = new RecordingHandler(new MemoryStoreAcceptedResult
        {
            Status = "queued",
            ReceiptId = "memory_write_1",
            Source = "api",
            InputMode = "typed",
            EntitiesRequested = 1,
        });
        var client = CreateClient(handler);

        var response = await client.QueueClaimsAsync(
            "Michael",
            new MemoryClaimExtractionResult
            {
                Entities =
                [
                    new MemoryExtractedEntity
                    {
                        Id = "codegraph",
                        Label = "CodeGraph",
                        Type = "project",
                    }
                ]
            });

        response.ReceiptId.ShouldBe("memory_write_1");
        handler.Requests.Count.ShouldBe(1);
        var request = handler.Requests[0];
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://memory.local/api/memory/claims/store?source=api");
        request.Headers.Contains(CodeGraphInternalServiceAuthenticationDefaults.HeaderName).ShouldBeTrue();
        var body = await request.Content!.ReadAsStringAsync();
        body.ShouldContain("\"entities\"");
        body.ShouldContain("\"codegraph\"");
    }

    [Fact]
    public async Task SearchAsync_SendsClampedLimitsInQueryString()
    {
        var handler = new RecordingHandler(new MemorySearchResult { Query = "CodeGraph" });
        var client = CreateClient(handler);

        var result = await client.SearchAsync("michael", "CodeGraph", entityLimit: 100, claimLimit: 0);

        result.Query.ShouldBe("CodeGraph");
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe("/api/memory/search?query=CodeGraph&entityLimit=25&claimLimit=1");
    }

    [Fact]
    public async Task GetWriteStatusAsync_ReturnsNullForNotFound()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, null);
        var client = CreateClient(handler);

        var receipt = await client.GetWriteStatusAsync("michael", "missing");

        receipt.ShouldBeNull();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_SendsClampedDiagnosticParameters()
    {
        var handler = new RecordingHandler(new MemoryDiagnosticsResult
        {
            Username = "default",
            HealthSignals = ["stale_queued_writes"],
        });
        var client = CreateClient(handler);

        var result = await client.GetDiagnosticsAsync("michael", staleAfterMinutes: 0, sampleLimit: 500);

        result.HealthSignals.ShouldBe(["stale_queued_writes"]);
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe(
            "/api/memory/diagnostics?staleAfterMinutes=1&sampleLimit=100");
    }

    [Fact]
    public async Task GetWriteDiagnosticsAsync_UsesWriteDiagnosticsRoute()
    {
        var handler = new RecordingHandler(new MemoryWriteDiagnosticsResult
        {
            Username = "default",
            QueuedCount = 2,
        });
        var client = CreateClient(handler);

        var result = await client.GetWriteDiagnosticsAsync("michael", staleAfterMinutes: 30, sampleLimit: 5);

        result.QueuedCount.ShouldBe(2);
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe(
            "/api/memory/writes/diagnostics?staleAfterMinutes=30&sampleLimit=5");
    }

    [Fact]
    public async Task DeleteBySourceAsync_PostsCleanupRequest()
    {
        var handler = new RecordingHandler(new MemoryCleanupResult
        {
            Scope = "source",
            DryRun = true,
            Sources = ["test"],
            ClaimsDeleted = 2,
        });
        var client = CreateClient(handler);

        var result = await client.DeleteBySourceAsync("michael", " test ", dryRun: true);

        result.ClaimsDeleted.ShouldBe(2);
        var request = handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.PathAndQuery.ShouldBe("/api/memory/cleanup/by-source");
        var body = await request.Content!.ReadAsStringAsync();
        body.ShouldContain("\"source\":\"test\"");
        body.ShouldContain("\"dryRun\":true");
    }

    [Fact]
    public async Task DeleteByIdsAsync_PostsNormalizedCleanupRequest()
    {
        var handler = new RecordingHandler(new MemoryCleanupResult
        {
            Scope = "explicit_items",
            DryRun = false,
            ClaimIds = ["claim_1"],
            EntityIds = ["entity_1"],
        });
        var client = CreateClient(handler);

        await client.DeleteByIdsAsync(
            "michael",
            [" claim_1 ", "", "claim_1"],
            [" entity_1 "],
            dryRun: false);

        var request = handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.PathAndQuery.ShouldBe("/api/memory/cleanup/by-ids");
        var body = await request.Content!.ReadAsStringAsync();
        body.ShouldContain("\"claimIds\":[\"claim_1\"]");
        body.ShouldContain("\"entityIds\":[\"entity_1\"]");
        body.ShouldContain("\"dryRun\":false");
    }

    [Fact]
    public async Task Client_ThrowsTypedExceptionForErrorResponse()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            new { error = "invalid_request", message = "No memory for you." });
        var client = CreateClient(handler);

        var ex = await Should.ThrowAsync<MemoryClientException>(() => client.SearchAsync("michael", "CodeGraph"));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.ErrorCode.ShouldBe("invalid_request");
        ex.Message.ShouldBe("No memory for you.");
    }

    private static HttpMemoryClient CreateClient(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://memory.local/")
        };
        var factory = new StubHttpClientFactory(httpClient);
        var options = Options.Create(new MemoryClientOptions
        {
            Audience = "codegraph-memory",
            MaxTransientAttempts = 1,
        });
        var tokenFactory = new InternalServiceTokenFactory(Options.Create(new InternalServiceAuthOptions
        {
            Enabled = true,
            HmacKey = "test-key-with-enough-entropy"
        }));

        return new HttpMemoryClient(factory, options, tokenFactory);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly HttpStatusCode _statusCode;
        private readonly object? _response;

        public RecordingHandler(object response)
            : this(HttpStatusCode.OK, response)
        {
        }

        public RecordingHandler(HttpStatusCode statusCode, object? response)
        {
            _statusCode = statusCode;
            _response = response;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequestAsync(request, cancellationToken).GetAwaiter().GetResult());
            var message = new HttpResponseMessage(_statusCode);
            if (_response is not null)
            {
                message.Content = new StringContent(
                    JsonSerializer.Serialize(_response, JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json");
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
