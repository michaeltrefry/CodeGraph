using System.Net;
using System.Text;
using System.Text.Json;
using CodeGraph.Models;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class GraphAssistantDreamBatchTests
{
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
        await assistant.CompleteAsync(new(provider,"scripted","instructions",[new("user","data")],DreamingService.Tools,100));
        using var body = JsonDocument.Parse(handler.Body!);
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBe(DreamingService.Tools.Count);
        foreach (var definition in DreamingService.Tools)
        {
            var tool = tools.Single(value => (provider == "openai" ? value.GetProperty("function") : value).GetProperty("name").GetString() == definition.Name);
            var actual = provider == "openai" ? tool.GetProperty("function").GetProperty("parameters") : tool.GetProperty("input_schema");
            // Compare the complete nested structure, including required lists, enums and refs.
            System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(definition.Parameters.GetRawText())).ShouldBeTrue(definition.Name);
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
                actual.GetProperty("anyOf").GetArrayLength().ShouldBe(6);
            }
        }
    }

    private sealed class HttpFactory(CaptureHandler handler) : IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,disposeHandler:false);}
    private sealed class CaptureHandler(string reply) : HttpMessageHandler
    {
        public string? Body {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Body=await request.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent(reply,Encoding.UTF8,"application/json")};}
    }
}
