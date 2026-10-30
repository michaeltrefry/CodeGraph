using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Models;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;

namespace CodeGraph.Services.Assistant;

/// <summary>Non-streaming tool turns, sharing the assistant's provider configuration and HTTP plumbing.</summary>
public partial class GraphAssistant : IDreamBatchModel
{
    public bool SupportsTools(string provider) => provider is "anthropic" or "openai" or "lmstudio";
    public async Task<DreamModelResponse> CompleteAsync(DreamModelRequest request,CancellationToken ct=default)
    {
        if(!SupportsTools(request.Provider)) throw new InvalidOperationException("Dreaming requires a tool-use provider.");
        if(request.Provider=="anthropic") return await CompleteDreamAnthropicAsync(request,ct);
        var messages=request.Messages.Select(m=>new OpenAiChatMessage {Role=m.Role,Content=m.Content,ToolCallId=m.ToolCallId,
            ToolCalls=m.ToolCalls?.Select(t=>new OpenAiToolCall {Id=t.Id,Function=new() {Name=t.Name,Arguments=t.Arguments.GetRawText()}}).ToList()}).ToList();
        var tools=request.Tools.Select(t=>new OpenAiToolDefinition {Function=new() {Name=t.Name,Description=t.Description,
            Parameters=JsonSerializer.Deserialize<OpenAiFunctionParameters>(t.Parameters,CodeGraphJsonDefaults.SnakeCase)!}}).ToList();
        var config=new LlmAssistantRuntimeConfig(request.Provider,request.Model,request.MaxTokens,1,null,null,true);
        var completion=await CompleteOpenAiCompatibleTurnAsync(httpClientFactory.CreateClient(),request.Provider,config,messages,tools,"dreamer","dreaming.run",ct,request.System);
        var message=completion.Choices.FirstOrDefault()?.Message??throw new InvalidOperationException("Provider returned no batch tool response.");
        return new(message.Content,message.ToolCalls?.Select(t=>new DreamToolCall(t.Id,t.Function?.Name??"",JsonSerializer.Deserialize<JsonElement>(t.Function?.Arguments??"{}"))).ToList()??[],
            completion.Usage?.PromptTokens??throw new InvalidOperationException("Provider omitted token usage; cannot enforce dreaming budget."),completion.Usage.CompletionTokens??0);
    }
    private async Task<DreamModelResponse> CompleteDreamAnthropicAsync(DreamModelRequest request,CancellationToken ct)
    {
        var provider=await ResolveProviderConfigAsync("anthropic",ct);
        var baseUrl=FirstNonEmpty(provider.EndpointUrl,TryGetAnthropicBaseUrl(options.Anthropic.MessagesApiUrl),"https://api.anthropic.com").TrimEnd('/');
        var messages=new List<object>();
        foreach(var m in request.Messages)
        {
            if(m.Role=="tool") messages.Add(new {role="user",content=new object[]{new {type="tool_result",tool_use_id=m.ToolCallId,content=m.Content??""}}});
            else if(m.ToolCalls is {Count:>0})
            {
                var content=new List<object>();
                if(!string.IsNullOrEmpty(m.Content)) content.Add(new {type="text",text=m.Content});
                content.AddRange(m.ToolCalls.Select(t=>(object)new {type="tool_use",id=t.Id,name=t.Name,input=t.Arguments}));
                messages.Add(new {role=m.Role,content});
            }
            else messages.Add(new {role=m.Role,content=m.Content??""});
        }
        using var httpRequest=new HttpRequestMessage(HttpMethod.Post,baseUrl+"/v1/messages");
        if(!string.IsNullOrWhiteSpace(provider.ApiKey)) httpRequest.Headers.Add("x-api-key",provider.ApiKey);
        httpRequest.Headers.Add("anthropic-version","2023-06-01");
        httpRequest.Content=JsonContent.Create(new {model=request.Model,max_tokens=request.MaxTokens,system=request.System,messages,
            tools=request.Tools.Select(t=>new {name=t.Name,description=t.Description,input_schema=t.Parameters})});
        using var response=await httpClientFactory.CreateClient().SendAsync(httpRequest,ct);
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root=json.RootElement;var text=new List<string>();var calls=new List<DreamToolCall>();
        foreach(var block in root.GetProperty("content").EnumerateArray())
        {
            if(block.GetProperty("type").GetString()=="text") text.Add(block.GetProperty("text").GetString()??"");
            else if(block.GetProperty("type").GetString()=="tool_use") calls.Add(new(block.GetProperty("id").GetString()!,block.GetProperty("name").GetString()!,block.GetProperty("input").Clone()));
        }
        var usage=root.GetProperty("usage");
        var input=usage.GetProperty("input_tokens").GetInt64();
        if(usage.TryGetProperty("cache_read_input_tokens",out var read)) input+=read.GetInt64();
        if(usage.TryGetProperty("cache_creation_input_tokens",out var creation)) input+=creation.GetInt64();
        return new(string.Join("\n",text),calls,input,usage.GetProperty("output_tokens").GetInt64());
    }
    internal async Task<object> ReadDreamSourceAsync(string repository,string path,long? nodeId,CancellationToken ct)
    {
        var lines=await RepoFileResolver.ReadAllLinesAsync(repository,path,sourceOptions,store)
            ??throw WorkException.NotFound("Source file was not found; it has not been verified.");
        ct.ThrowIfCancellationRequested();
        DreamExecutionScope.Current!.MarkSourceRead(repository,path,nodeId);
        return new {untrusted_data=new {repository,path,node_id=nodeId,source=string.Join("\n",lines)}};
    }
    internal Task<string> SearchDreamGraphAsync(string repository,string pattern,string? label,int limit) =>
        SearchGraphAsync(JsonSerializer.SerializeToElement(new {repository,namePattern=pattern,label,limit}));
}
