using System.Net;
using System.Text;
using System.Text.Json;
using CodeGraph.Models;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit.Abstractions;

namespace CodeGraph.Tests.Services;

public sealed partial class GraphAssistantDreamBatchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages/batches")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages/batches")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages/batches")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages/batches")]
    [InlineData("https://proxy.example/anthropic/v1", "https://proxy.example/anthropic/v1/messages", "https://proxy.example/anthropic/v1/messages/batches")]
    [InlineData("https://proxy.example/anthropic/v2/", "https://proxy.example/anthropic/v2/messages", "https://proxy.example/anthropic/v2/messages/batches")]
    [InlineData("https://proxy.example/anthropic/", "https://proxy.example/anthropic/messages", "https://proxy.example/anthropic/messages/batches")]
    [InlineData("https://proxy.example/anthropic/v1/messages", "https://proxy.example/anthropic/v1/messages", "https://proxy.example/anthropic/v1/messages/batches")]
    [InlineData("https://proxy.example/anthropic/v1/messages/", "https://proxy.example/anthropic/v1/messages", "https://proxy.example/anthropic/v1/messages/batches")]
    [InlineData("https://proxy.example/anthropic/v1/messages/batches/", "https://proxy.example/anthropic/v1/messages", "https://proxy.example/anthropic/v1/messages/batches")]
    [InlineData("https://proxy.example/gateway%2Ftenant/v1?route=tenant", "https://proxy.example/gateway%2Ftenant/v1/messages?route=tenant", "https://proxy.example/gateway%2Ftenant/v1/messages/batches?route=tenant")]
    [InlineData(null, "https://configured.example/proxy/v2/messages?route=messages/", "https://configured.example/batch-proxy/v3/messages/batches?route=batches/")]
    public async Task Anthropic_dream_batch_uses_the_configured_api_path_once(string? endpoint, string expected, string expectedBatch)
    {
        var handler = new CaptureHandler("""{"model":"dream-model","content":[{"type":"text","text":"done"}],"usage":{"input_tokens":11,"output_tokens":7}}""");
        var options = new AnalysisOptions();
        options.Anthropic.MessagesApiUrl = "https://configured.example/proxy/v2/messages?route=messages/";
        options.Anthropic.BatchApiBaseUrl = "https://configured.example/batch-proxy/v3/messages/batches?route=batches/";
        var resolver = new FixedAnthropicConfig(endpoint);
        var assistant = new GraphAssistant(null!, new HttpFactory(handler), null!, null!, null!, Options.Create(new RepositorySourceOptions()),
            Options.Create(options), null!, null!, NullLogger<GraphAssistant>.Instance, providerConfigResolver: resolver);

        var result = await assistant.CompleteAsync(new("anthropic", "dream-model", "instructions", [new("user", "untrusted data")], [], 321));

        handler.Uri!.AbsoluteUri.ShouldBe(expected);
        handler.Method.ShouldBe(HttpMethod.Post);
        handler.ApiKey.ShouldBe("configured-key");
        handler.ApiVersion.ShouldBe("2023-06-01");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("dream-model");
        body.RootElement.GetProperty("system").GetString().ShouldBe("instructions");
        body.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(321);
        body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("untrusted data");
        body.RootElement.TryGetProperty("stream", out _).ShouldBeFalse();
        result.Text.ShouldBe("done");
        result.InputTokens.ShouldBe(11);
        result.OutputTokens.ShouldBe(7);

        // The ordinary analyzer uses the same API-base contract; batch creation, status and
        // results must keep proxy prefixes and query routing as well as the message endpoint.
        AnthropicAnalysisProvider Analyzer(CaptureHandler capture) => new(new HttpFactory(capture),
            new AnthropicCircuitBreaker(NullLogger<AnthropicCircuitBreaker>.Instance), Options.Create(options),
            NullLogger<AnthropicAnalysisProvider>.Instance, resolver);
        var analysisHandler = new CaptureHandler("""{"model":"analysis-model","content":[{"type":"text","text":"analysis"}]}""");
        var analysis = await Analyzer(analysisHandler).ExecuteAsync(new("analysis instructions", "analysis input"), new("analysis-model", 421));
        analysis.Text.ShouldBe("analysis");
        analysisHandler.Uri!.AbsoluteUri.ShouldBe(expected);
        analysisHandler.ApiKey.ShouldBe("configured-key");
        analysisHandler.ApiVersion.ShouldBe("2023-06-01");
        using var analysisBody = JsonDocument.Parse(analysisHandler.Body!);
        analysisBody.RootElement.GetProperty("model").GetString().ShouldBe("analysis-model");
        analysisBody.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(421);
        analysisBody.RootElement.GetProperty("system").GetString().ShouldBe("analysis instructions");

        var batchHandler = new CaptureHandler("""{"id":"batch-1","processing_status":"ended"}""");
        var batchAnalyzer = Analyzer(batchHandler);
        var submitted = await batchAnalyzer.SubmitBatchAsync([new("item-1", new("batch instructions", "batch input"))], new("batch-model", 521));
        submitted.BatchId.ShouldBe("batch-1");
        batchHandler.Uri!.AbsoluteUri.ShouldBe(expectedBatch);
        batchHandler.Method.ShouldBe(HttpMethod.Post);
        batchHandler.ApiKey.ShouldBe("configured-key");
        using var batchBody = JsonDocument.Parse(batchHandler.Body!);
        var batchRequest = batchBody.RootElement.GetProperty("requests")[0];
        batchRequest.GetProperty("custom_id").GetString().ShouldBe("item-1");
        batchRequest.GetProperty("params").GetProperty("model").GetString().ShouldBe("batch-model");
        batchRequest.GetProperty("params").GetProperty("max_tokens").GetInt32().ShouldBe(521);
        (await batchAnalyzer.GetBatchStatusAsync("batch-1")).IsCompleted.ShouldBeTrue();
        batchHandler.Method.ShouldBe(HttpMethod.Get);
        batchHandler.Uri!.AbsoluteUri.ShouldBe(expectedBatch.Replace("/messages/batches", "/messages/batches/batch-1", StringComparison.Ordinal));
        var resultsHandler = new CaptureHandler("""{"custom_id":"item-1","result":{"type":"succeeded","message":{"model":"batch-model","content":[{"type":"text","text":"batch result"}]}}}""");
        (await Analyzer(resultsHandler).GetBatchResultsAsync("batch-1")).Single().Text.ShouldBe("batch result");
        resultsHandler.Method.ShouldBe(HttpMethod.Get);
        resultsHandler.Uri!.AbsoluteUri.ShouldBe(expectedBatch.Replace("/messages/batches", "/messages/batches/batch-1/results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blank_anthropic_endpoint_options_keep_the_default_messages_and_batch_routes()
    {
        var options = new AnalysisOptions();
        options.Anthropic.MessagesApiUrl = "";
        options.Anthropic.BatchApiBaseUrl = "";
        var handler = new CaptureHandler("""{"id":"batch-1","processing_status":"ended","content":[{"type":"text","text":"done"}],"usage":{"input_tokens":1,"output_tokens":1}}""");
        var assistant = new GraphAssistant(null!, new HttpFactory(handler), null!, null!, null!, Options.Create(new RepositorySourceOptions()),
            Options.Create(options), null!, null!, NullLogger<GraphAssistant>.Instance);
        await assistant.CompleteAsync(new("anthropic", "scripted", "instructions", [new("user", "data")], [], 100));
        handler.Uri!.AbsoluteUri.ShouldBe("https://api.anthropic.com/v1/messages");
        var analyzer = new AnthropicAnalysisProvider(new HttpFactory(handler), new AnthropicCircuitBreaker(NullLogger<AnthropicCircuitBreaker>.Instance),
            Options.Create(options), NullLogger<AnthropicAnalysisProvider>.Instance);
        await analyzer.SubmitBatchAsync([new("item-1", new("system", "data"))], new("scripted", 100));
        handler.Uri!.AbsoluteUri.ShouldBe("https://api.anthropic.com/v1/messages/batches");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task Batch_provider_preserves_complete_tool_schema_system_instructions_and_usage_without_streaming(string provider)
    {
        var reply=provider=="openai"
            ? """{"model":"scripted","choices":[{"message":{"role":"assistant","content":"done","tool_calls":[{"id":"call1","type":"function","function":{"name":"verify","arguments":"{\"evidence\":\"ok\"}"}}]}}],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}"""
            : """{"model":"scripted","content":[{"type":"text","text":"done"},{"type":"tool_use","id":"call1","name":"verify","input":{"evidence":"ok"}}],"usage":{"input_tokens":11,"output_tokens":7}}""";
        var handler=new CaptureHandler(reply);
        var assistant=new GraphAssistant(null!,new HttpFactory(handler),null!,null!,null!,Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions()),null!,null!,NullLogger<GraphAssistant>.Instance);
        var schema=JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false,"$defs":{"evidence":{"type":"string","minLength":1}},"properties":{"evidence":{"$ref":"#/$defs/evidence"}},"required":["evidence"]}""");

        var result=await assistant.CompleteAsync(new(provider,"scripted","versioned instructions",[new("user","untrusted project data")],[new("verify","Verify using evidence",schema)],321));

        using var request=JsonDocument.Parse(handler.Body!);
        var root=request.RootElement;
        root.GetProperty("model").GetString().ShouldBe("scripted");
        (root.TryGetProperty("stream",out var streaming)&&streaming.ValueKind==JsonValueKind.True).ShouldBeFalse();
        var actualSchema=provider=="openai"?root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters"):
            root.GetProperty("tools")[0].GetProperty("input_schema");
        actualSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        actualSchema.GetProperty("$defs").GetProperty("evidence").GetProperty("minLength").GetInt32().ShouldBe(1);
        actualSchema.GetProperty("properties").GetProperty("evidence").GetProperty("$ref").GetString().ShouldBe("#/$defs/evidence");
        if(provider=="openai")
        {
            root.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("system");
            root.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("versioned instructions");
            root.GetProperty("max_completion_tokens").GetInt32().ShouldBe(321);
        }
        else
        {
            root.GetProperty("system").GetString().ShouldBe("versioned instructions");
            root.GetProperty("max_tokens").GetInt32().ShouldBe(321);
        }
        result.Text.ShouldBe("done");result.InputTokens.ShouldBe(11);result.OutputTokens.ShouldBe(7);
        result.ToolCalls.Single().Name.ShouldBe("verify");
        result.ToolCalls.Single().Arguments.GetProperty("evidence").GetString().ShouldBe("ok");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task Actual_dream_contract_reaches_the_provider_with_all_nested_fields_and_required_types(string provider)
    {
        var reply = provider == "openai"
            ? """{"choices":[{"message":{"role":"assistant","content":"done"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""
            : """{"content":[{"type":"text","text":"done"}],"usage":{"input_tokens":1,"output_tokens":1}}""";
        var handler = new CaptureHandler(reply);
        var assistant = new GraphAssistant(null!,new HttpFactory(handler),null!,null!,null!,Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions()),null!,null!,NullLogger<GraphAssistant>.Instance);
        var canonical = DreamingService.Tools.ToDictionary(tool => tool.Name, tool => tool.Parameters.GetRawText());
        DreamModelMessage[] messages = [new("user", "data")];
        await assistant.CompleteAsync(new(provider,"scripted","instructions",messages,DreamingService.Tools,100));
        AssertWireBudget(handler, "instructions", messages, DreamingService.Tools);
        using var body = JsonDocument.Parse(handler.Body!);
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBe(DreamingService.Tools.Count);
        foreach (var definition in DreamingService.Tools)
        {
            var tool = tools.Single(value => (provider == "openai" ? value.GetProperty("function") : value).GetProperty("name").GetString() == definition.Name);
            var actual = provider == "openai" ? tool.GetProperty("function").GetProperty("parameters") : tool.GetProperty("input_schema");
            var expected = System.Text.Json.Nodes.JsonNode.Parse(definition.Parameters.GetRawText())!.AsObject();
            if (provider == "anthropic")
            {
                foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
                {
                    actual.TryGetProperty(keyword, out _).ShouldBeFalse(definition.Name);
                    if (expected.Remove(keyword, out var rules))
                        tool.GetProperty("description").GetString()!.ShouldContain(definition.Parameters.GetProperty(keyword).GetRawText());
                }
            }
            // Every other byte of schema structure remains, including nested compositions,
            // required lists, enums, definitions and refs. OpenAI remains exact throughout.
            System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
                expected).ShouldBeTrue(definition.Name);
            definition.Parameters.GetRawText().ShouldBe(canonical[definition.Name]);
            if (definition.Name == "dream_verify")
            {
                var evidence = actual.GetProperty("properties").GetProperty("evidence").GetProperty("items");
                evidence.GetProperty("required").GetArrayLength().ShouldBe(3);
                evidence.GetProperty("properties").GetProperty("kind").GetProperty("enum").GetArrayLength().ShouldBe(10);
                evidence.GetProperty("properties").GetProperty("reference").GetProperty("type").GetString().ShouldBe("string");
            }
            if (definition.Name == "dream_propose")
            {
                actual.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldContain("target");
                actual.GetProperty("properties").GetProperty("evidence").GetProperty("items").GetProperty("required").GetArrayLength().ShouldBe(2);
                if (provider == "openai") actual.GetProperty("anyOf").GetArrayLength().ShouldBe(6);
                else tool.GetProperty("description").GetString()!.ShouldContain("at least one");
            }
        }
    }

    [Fact]
    public async Task Anthropic_parallel_results_share_one_user_message_and_stop_at_the_next_assistant_turn()
    {
        var handler = new CaptureHandler("""{"content":[{"type":"tool_use","id":"next-one","name":"dream_read","input":{}},{"type":"tool_use","id":"next-two","name":"dream_work_trail","input":{}},{"type":"tool_use","id":"next-three","name":"dream_lessons","input":{"view":"recurrence"}}],"usage":{"input_tokens":11,"cache_creation_input_tokens":2,"cache_read_input_tokens":3,"output_tokens":7}}""");
        var assistant = new GraphAssistant(null!, new HttpFactory(handler), null!, null!, null!, Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions()), null!, null!, NullLogger<GraphAssistant>.Instance);
        DreamToolCall Call(string id) => new(id, "dream_read", JsonSerializer.SerializeToElement(new { detail = "full" }));
        DreamModelMessage[] messages = [new("user", "input"), new("assistant", null, [Call("one"), Call("two"), Call("three")]),
            new("tool", "result one 界", ToolCallId: "one"), new("tool", "{\"error\":\"invalid_argument\"}", ToolCallId: "two"), new("tool", "result three", ToolCallId: "three"),
            new("assistant", "next turn", [Call("four")]), new("tool", "result four", ToolCallId: "four"), new("assistant", "boundary"), new("user", "follow up")];

        var result = await assistant.CompleteAsync(new("anthropic", "scripted", "instructions", messages, DreamingService.Tools, 100));

        using var body = JsonDocument.Parse(handler.Body!);
        var actual = body.RootElement.GetProperty("messages");
        actual.GetArrayLength().ShouldBe(7);
        actual[2].GetProperty("role").GetString().ShouldBe("user");
        var results = actual[2].GetProperty("content").EnumerateArray().ToArray();
        results.Select(item => item.GetProperty("type").GetString()).ShouldAllBe(type => type == "tool_result");
        results.Select(item => item.GetProperty("tool_use_id").GetString()).ShouldBe(new[] { "one", "two", "three" });
        results.Select(item => item.GetProperty("content").GetString()).ShouldBe(messages.Skip(2).Take(3).Select(message => message.Content));
        actual[3].GetProperty("role").GetString().ShouldBe("assistant");
        actual[3].GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("next turn");
        actual[4].GetProperty("content").GetArrayLength().ShouldBe(1);
        actual[4].GetProperty("content")[0].GetProperty("tool_use_id").GetString().ShouldBe("four");
        actual[5].GetProperty("content").GetString().ShouldBe("boundary");
        actual[6].GetProperty("content").GetString().ShouldBe("follow up");
        result.ToolCalls.Select(call => call.Id).ShouldBe(new[] { "next-one", "next-two", "next-three" });
        result.ToolCalls[2].Arguments.GetProperty("view").GetString().ShouldBe("recurrence");
        result.InputTokens.ShouldBe(16);
        result.OutputTokens.ShouldBe(7);
        AssertWireBudget(handler, "instructions", messages, DreamingService.Tools);
    }

    [Fact]
    public async Task Anthropic_root_composition_rules_are_guidance_while_nested_rules_and_definitions_remain_intact()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"anyOf":[{"type":"string"},{"type":"integer"}],"$ref":"#/$defs/value"}},"$defs":{"value":{"type":"string"}},"required":["value"],"anyOf":[{"required":["value"]}],"oneOf":[{"properties":{"value":{"const":"one"}}}],"allOf":[{"properties":{"value":{"minLength":1}}}]}""");
        var original = schema.GetRawText();
        var handler = new CaptureHandler("""{"content":[{"type":"text","text":"done"}],"usage":{"input_tokens":1,"output_tokens":1}}""");
        var assistant = new GraphAssistant(null!, new HttpFactory(handler), null!, null!, null!, Options.Create(new RepositorySourceOptions()),
            Options.Create(new AnalysisOptions()), null!, null!, NullLogger<GraphAssistant>.Instance);
        await assistant.CompleteAsync(new("anthropic", "scripted", "instructions", [new("user", "data")], [new("example", "Original guidance", schema)], 100));
        using var body = JsonDocument.Parse(handler.Body!);
        var tool = body.RootElement.GetProperty("tools")[0];
        var description = tool.GetProperty("description").GetString()!;
        description.ShouldStartWith("Original guidance");
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            tool.GetProperty("input_schema").TryGetProperty(keyword, out _).ShouldBeFalse();
            description.ShouldContain(schema.GetProperty(keyword).GetRawText());
        }
        description.ShouldContain("at least one"); description.ShouldContain("exactly one"); description.ShouldContain("all branches");
        var expected = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" }) expected.Remove(keyword);
        System.Text.Json.Nodes.JsonNode.DeepEquals(expected, System.Text.Json.Nodes.JsonNode.Parse(tool.GetProperty("input_schema").GetRawText())).ShouldBeTrue();
        schema.GetRawText().ShouldBe(original);
    }

    private void AssertWireBudget(CaptureHandler handler, string system, IReadOnlyList<DreamModelMessage> messages, IReadOnlyList<DreamToolDefinition> tools)
    {
        var actual = Encoding.UTF8.GetByteCount(handler.Body!);
        var reserved = DreamingService.ConservativeInputBudget(system, messages, tools);
        output.WriteLine($"Wire UTF8 bytes={actual}; conservative input reserve={reserved}");
        ((long)actual).ShouldBeLessThanOrEqualTo(reserved);
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,disposeHandler:false);}
    private sealed class FixedAnthropicConfig(string? endpoint) : IDbBackedLlmProviderConfigResolver
    {
        public Task<LlmProviderRuntimeConfig> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(new LlmProviderRuntimeConfig(providerKey, "configured-key", endpoint, "2023-06-01", "configured-model", ["configured-model"], true, true));
    }
    private sealed class CaptureHandler(string reply) : HttpMessageHandler
    {
        public string? Body {get;private set;}
        public Uri? Uri {get;private set;}
        public HttpMethod? Method {get;private set;}
        public string? ApiKey {get;private set;}
        public string? ApiVersion {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Uri=request.RequestUri; Method=request.Method;
            ApiKey=request.Headers.TryGetValues("x-api-key",out var keys)?keys.Single():null;
            ApiVersion=request.Headers.TryGetValues("anthropic-version",out var versions)?versions.Single():null;
            Body=request.Content is null?null:await request.Content.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK){Content=new StringContent(reply,Encoding.UTF8,"application/json")};
        }
    }
}
