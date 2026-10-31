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
            Parameters=t.Parameters.Clone()}}).ToList();
        var config=new LlmAssistantRuntimeConfig(request.Provider,request.Model,request.MaxTokens,1,null,null,true);
        var completion=await CompleteOpenAiCompatibleTurnAsync(httpClientFactory.CreateClient(),request.Provider,config,messages,tools,"dreamer","dreaming.run",ct,request.System);
        var message=completion.Choices.FirstOrDefault()?.Message??throw new InvalidOperationException("Provider returned no batch tool response.");
        return new(message.Content,message.ToolCalls?.Select(t=>new DreamToolCall(t.Id,t.Function?.Name??"",JsonSerializer.Deserialize<JsonElement>(t.Function?.Arguments??"{}"))).ToList()??[],
            completion.Usage?.PromptTokens??throw new InvalidOperationException("Provider omitted token usage; cannot enforce dreaming budget."),completion.Usage.CompletionTokens??0);
    }
    private async Task<DreamModelResponse> CompleteDreamAnthropicAsync(DreamModelRequest request,CancellationToken ct)
    {
        var provider=await ResolveProviderConfigAsync("anthropic",ct);
        var url=AnthropicEndpointUrls.Messages(provider.EndpointUrl,options.Anthropic.MessagesApiUrl);
        var messages=new List<object>();
        for(var index=0;index<request.Messages.Count;)
        {
            var m=request.Messages[index++];
            if(m.Role=="tool")
            {
                // Anthropic requires every result of a parallel tool turn in one following
                // user message. Stop at the next assistant/user boundary, retaining order.
                var results=new List<object>();
                while(true)
                {
                    results.Add(new {type="tool_result",tool_use_id=m.ToolCallId,content=m.Content??""});
                    if(index>=request.Messages.Count||request.Messages[index].Role!="tool") break;
                    m=request.Messages[index++];
                }
                messages.Add(new {role="user",content=results});
            }
            else if(m.ToolCalls is {Count:>0})
            {
                var content=new List<object>();
                if(!string.IsNullOrEmpty(m.Content)) content.Add(new {type="text",text=m.Content});
                content.AddRange(m.ToolCalls.Select(t=>(object)new {type="tool_use",id=t.Id,name=t.Name,input=t.Arguments}));
                messages.Add(new {role=m.Role,content});
            }
            else messages.Add(new {role=m.Role,content=m.Content??""});
        }
        using var httpRequest=new HttpRequestMessage(HttpMethod.Post,url);
        if(!string.IsNullOrWhiteSpace(provider.ApiKey)) httpRequest.Headers.Add("x-api-key",provider.ApiKey);
        httpRequest.Headers.Add("anthropic-version","2023-06-01");
        httpRequest.Content=JsonContent.Create(new {model=request.Model,max_tokens=request.MaxTokens,system=request.System,messages,
            tools=request.Tools.Select(BuildDreamAnthropicTool)});
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

    private static object BuildDreamAnthropicTool(DreamToolDefinition tool)
    {
        var schema=JsonNode.Parse(tool.Parameters.GetRawText())!.AsObject();
        var description=tool.Description;
        // Anthropic rejects composition keywords at the input-schema root. Keep their full
        // branch rules as mandatory guidance; canonical schemas and server validation stay
        // authoritative. All nested constraints, definitions, properties and refs remain.
        foreach(var (keyword,meaning) in new[] {("anyOf","at least one branch must match"),
            ("oneOf","exactly one branch must match"),("allOf","all branches must match")})
        {
            if(!schema.Remove(keyword)) continue;
            description+=$"\nMandatory argument rules, validated by the server: {keyword} ({meaning}): {tool.Parameters.GetProperty(keyword).GetRawText()}";
        }
        return new {name=tool.Name,description,input_schema=JsonSerializer.SerializeToElement(schema)};
    }
    internal async Task<object> ReadDreamSourceAsync(string repository,string path,long? nodeId,CancellationToken ct)
    {
        var indexed=await store.GetFileHashesAsync(repository);
        indexed.TryGetValue(path,out var indexHash);
        await using var stream=await RepoFileResolver.OpenReadAsync(repository,path,sourceOptions,store,ct)
            ??throw WorkException.NotFound("Source file was not found; it has not been verified.");
        using var bytes=new MemoryStream();await stream.CopyToAsync(bytes,ct);
        var raw=bytes.ToArray();
        // XxHash3 is the indexing pipeline's byte hash. Older imported baselines may use SHA256.
        var contentHash=indexHash?.Length==64?Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)):
            Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(raw));
        DreamExecutionScope.Current!.MarkSourceRead(repository,path,nodeId,contentHash,indexHash);
        using var reader=new StreamReader(new MemoryStream(raw),detectEncodingFromByteOrderMarks:true);
        var source=await reader.ReadToEndAsync(ct);
        return new {untrusted_data=new {repository,path,node_id=nodeId,content_hash=contentHash,indexed_hash=indexHash,source}};
    }
}
