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

    private sealed class HttpFactory(CaptureHandler handler) : IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,disposeHandler:false);}
    private sealed class CaptureHandler(string reply) : HttpMessageHandler
    {
        public string? Body {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Body=await request.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent(reply,Encoding.UTF8,"application/json")};}
    }
}
