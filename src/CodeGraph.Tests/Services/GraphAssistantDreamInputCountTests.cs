using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed partial class GraphAssistantDreamBatchTests
{
    [Theory]
    [InlineData("https://api.example", "https://api.example/v1/messages")]
    [InlineData("https://api.example/v1/", "https://api.example/v1/messages")]
    [InlineData("https://proxy.example/gateway%2Ftenant/v2/messages?route=tenant/", "https://proxy.example/gateway%2Ftenant/v2/messages?route=tenant/")]
    public async Task Prepared_count_and_generation_share_immutable_wire_and_configuration(string endpoint, string expected)
    {
        var handler = new CountCapture();
        var resolver = new SnapshotConfig(endpoint);
        var options = new AnalysisOptions();
        var assistant = CountAssistant(handler, resolver, options);
        var messages = new List<DreamModelMessage> { new("user", "Original 界 input") };
        var tools = DreamingService.Tools.ToList();
        var preparing = assistant.PrepareAsync(new("anthropic", "chosen-model", "Original system", messages, tools, 8192));
        await resolver.Entered.Task;
        // Mutations while config awaits must not change the already prepared input.
        messages.Add(new("user", "Do not send")); tools.Clear();
        resolver.Release.SetResult();
        var prepared = (await preparing)!;
        resolver.Current = resolver.Current with { EndpointUrl = "https://changed.example/v9", ApiKey = "changed-key", ApiVersion = "changed-version" };
        options.Anthropic.MessagesApiUrl = "https://changed-fallback.example/messages";

        (await prepared.CountInputTokensAsync()).ShouldBe(9000);
        var result = await prepared.CompleteAsync(123);

        resolver.Calls.ShouldBe(1);
        handler.Requests.Count.ShouldBe(2);
        var counted = handler.Requests[0]; var generated = handler.Requests[1];
        counted.Uri.ShouldBe(expected.Replace("/messages", "/messages/count_tokens", StringComparison.Ordinal));
        generated.Uri.ShouldBe(expected);
        foreach (var request in handler.Requests)
        {
            request.Key.ShouldBe("original-key"); request.Version.ShouldBe("2026-10-21");
        }
        var countBody = JsonNode.Parse(counted.Body)!.AsObject();
        var sendBody = JsonNode.Parse(generated.Body)!.AsObject();
        countBody.ContainsKey("max_tokens").ShouldBeFalse();
        sendBody["max_tokens"]!.GetValue<int>().ShouldBe(123);
        sendBody.Remove("max_tokens");
        JsonNode.DeepEquals(countBody, sendBody).ShouldBeTrue();
        countBody["model"]!.GetValue<string>().ShouldBe("chosen-model");
        countBody["messages"]!.AsArray().Count.ShouldBe(1);
        countBody["tools"]!.AsArray().Count.ShouldBe(DreamingService.Tools.Count);
        countBody.ToJsonString().ShouldNotContain("Do not send");
        foreach (var tool in countBody["tools"]!.AsArray())
            tool!["input_schema"]!.AsObject().ContainsKey("anyOf").ShouldBeFalse();
        result.InputTokens.ShouldBe(7500); // Actual uncached + cache creation + cache reads, not 9000 preflight.
        result.OutputTokens.ShouldBe(10);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid json")]
    [InlineData("{\"input_tokens\":0}")]
    [InlineData("{\"input_tokens\":-1}")]
    [InlineData("{\"input_tokens\":1.5}")]
    [InlineData("{\"input_tokens\":\"9000\"}")]
    [InlineData("{\"input_tokens\":1e100}")]
    [InlineData("{\"input_tokens\":9000,\"input_tokens\":1}")]
    public async Task Malformed_counts_are_unavailable_and_never_generate(string reply)
    {
        var handler = new CountCapture { CountReply = reply };
        var turn = await PrepareCountAsync(handler);
        (await turn.CountInputTokensAsync()).ShouldBeNull();
        handler.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Unavailable_count_endpoint_never_generates_implicitly(int status)
    {
        var handler = new CountCapture { CountStatus = (HttpStatusCode)status };
        (await (await PrepareCountAsync(handler)).CountInputTokensAsync()).ShouldBeNull();
        handler.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Count_transport_failure_is_unavailable(string failure)
    {
        var handler = new CountCapture { CountFailure = failure == "network" ? new HttpRequestException("network") : new TaskCanceledException("timeout") };
        (await (await PrepareCountAsync(handler)).CountInputTokensAsync()).ShouldBeNull();
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Caller_cancellation_propagates_from_count_without_generation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CountCapture { BeforeCount = cancellation.Cancel };
        var turn = await PrepareCountAsync(handler);
        await Should.ThrowAsync<OperationCanceledException>(() => turn.CountInputTokensAsync(cancellation.Token));
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Counts_are_not_cached_between_attempts_or_changed_turns()
    {
        var handler = new CountCapture();
        var turn = await PrepareCountAsync(handler);
        (await turn.CountInputTokensAsync()).ShouldBe(9000);
        handler.CountReply = "{\"input_tokens\":10000}";
        (await turn.CountInputTokensAsync()).ShouldBe(10000);
        var another = await PrepareCountAsync(handler, "different input");
        (await another.CountInputTokensAsync()).ShouldBe(10000);
        handler.Requests.Count.ShouldBe(3);
        handler.Requests[0].Body.ShouldNotBe(handler.Requests[2].Body);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("lmstudio")]
    public async Task Other_providers_keep_the_existing_fallback_without_count_requests(string provider)
    {
        var handler = new CountCapture();
        var config = new SnapshotConfig("https://unused.example");
        (await CountAssistant(handler, config).PrepareAsync(new(provider, "model", "system", [new("user", "data")], [], 8192))).ShouldBeNull();
        handler.Requests.ShouldBeEmpty(); config.Calls.ShouldBe(0);
    }

    private static GraphAssistant CountAssistant(CountCapture handler, SnapshotConfig resolver, AnalysisOptions? options = null) =>
        new(null!, new HttpFactory(handler), null!, null!, null!, Options.Create(new RepositorySourceOptions()), Options.Create(options ?? new AnalysisOptions()),
            null!, null!, NullLogger<GraphAssistant>.Instance, providerConfigResolver: resolver);

    private static async Task<IDreamPreparedTurn> PrepareCountAsync(CountCapture handler, string content = "input")
    {
        var config = new SnapshotConfig("https://api.example/v1"); config.Release.SetResult();
        return (await CountAssistant(handler, config).PrepareAsync(new("anthropic", "model", "system", [new("user", content)], DreamingService.Tools, 8192)))!;
    }

    private sealed class SnapshotConfig(string endpoint) : IDbBackedLlmProviderConfigResolver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public LlmProviderRuntimeConfig Current { get; set; } = new("anthropic", "original-key", endpoint, "2026-10-21", "configured-model", [], true, true);
        public async Task<LlmProviderRuntimeConfig> GetProviderAsync(string providerKey, CancellationToken ct = default)
        {
            Calls++; Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return Current;
        }
    }

    private sealed record CapturedCountRequest(string Uri, string Key, string Version, string Body);
    private sealed class CountCapture : HttpMessageHandler
    {
        public string CountReply { get; set; } = "{\"input_tokens\":9000}";
        public HttpStatusCode CountStatus { get; set; } = HttpStatusCode.OK;
        public Exception? CountFailure { get; set; }
        public Action? BeforeCount { get; set; }
        public List<CapturedCountRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.GetValues("x-api-key").Single(), request.Headers.GetValues("anthropic-version").Single(), await request.Content!.ReadAsStringAsync(ct)));
            if (request.RequestUri.AbsolutePath.EndsWith("/count_tokens", StringComparison.Ordinal))
            {
                BeforeCount?.Invoke(); ct.ThrowIfCancellationRequested();
                if (CountFailure is not null) throw CountFailure;
                return new(CountStatus) { Content = new StringContent(CountReply, Encoding.UTF8, "application/json") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"content":[{"type":"text","text":"done"}],"usage":{"input_tokens":7000,"cache_creation_input_tokens":300,"cache_read_input_tokens":200,"output_tokens":10}}""", Encoding.UTF8, "application/json") };
        }
    }
}
