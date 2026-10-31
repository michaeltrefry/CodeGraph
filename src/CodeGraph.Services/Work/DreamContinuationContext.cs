using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

// Private persistence payload. Raw action JSON preserves fragment character offsets across attempts.
internal sealed class DreamContinuationState
{
    public int Version { get; set; } = 1;
    public DateTime SweepStartedAt { get; set; }
    public DateTime? DigestSince { get; set; }
    public int BaseRevision { get; set; }
    public int ProjectRevision { get; set; }
    public int DeliveredPrefix { get; set; }
    [JsonIgnore] public List<DreamModelMessage> Messages { get; set; } = [];
    public List<DreamFrozenMessage> Conversation { get=>Messages.Select(DreamFrozenMessage.Freeze).ToList(); set=>Messages=value.Select(m=>m.Thaw()).ToList(); }
    public List<DreamFrozenAction> Replay { get; set; } = [];
    [JsonIgnore] public List<DreamPendingCall> Pending { get; set; } = [];
    public List<DreamFrozenPending> PendingCalls { get=>Pending.Select(p=>new DreamFrozenPending(p.ExecutionId,p.OriginRunId,DreamFrozenCall.Freeze(p.Call))).ToList(); set=>Pending=value.Select(p=>new DreamPendingCall(p.ExecutionId,p.OriginRunId,p.Call.Thaw())).ToList(); }
    public JsonElement ReadWindows { get; set; } = JsonSerializer.SerializeToElement(new { });

    public static DreamContinuationState Read(JsonElement json)
    {
        var state=json.Deserialize<DreamContinuationState>(WorkJson.Options)
            ??throw WorkException.Invalid("The dreaming checkpoint is empty.");
        if(state.Version!=1||state.SweepStartedAt==default||state.Messages is null||state.Replay is null||state.Pending is null
            ||state.Messages.Count==0||state.Messages[0].Role!="user"||state.DeliveredPrefix<0||state.DeliveredPrefix>state.Messages.Count
            ||state.Messages.Count(m=>m.Role=="tool")!=state.Replay.Count
            ||state.Pending.Any(p=>p.ExecutionId==Guid.Empty||p.OriginRunId==Guid.Empty)
            ||state.Pending.Select(p=>p.ExecutionId).Distinct().Count()!=state.Pending.Count)
            throw WorkException.Invalid("Unsupported or inconsistent dreaming checkpoint; it was retained for recovery.");
        return state;
    }

    public void RehydrateArchives(IReadOnlyList<DreamAction> actions)
    {
        var ordinal=0;
        for(var i=0;i<Messages.Count;i++)
        {
            if(Messages[i].Role!="tool")continue;
            var action=actions[ordinal++];
            if(Messages[i].Content is not {} text)continue;
            using var value=JsonDocument.Parse(text);
            if(value.RootElement.ValueKind==JsonValueKind.Object
                &&value.RootElement.TryGetProperty("replay_archive",out var archived)&&archived.ValueKind==JsonValueKind.True)
                Messages[i]=Messages[i] with {Content=DreamModelContext.ModelJson(action.Result)};
        }
    }
}
internal sealed record DreamPendingCall(Guid ExecutionId,Guid OriginRunId,DreamToolCall Call);
internal sealed record DreamFrozenAction(string Tool,string ArgumentsJson,string ResultJson,IReadOnlyList<DreamEvidence> Evidence,
    DateTime CreatedAt,Guid? ExecutionId=null)
{
    public static DreamFrozenAction Freeze(DreamAction action)=>new(action.Tool,action.Arguments.GetRawText(),action.Result.GetRawText(),action.Evidence,action.CreatedAt,action.ExecutionId);
    public DreamAction Thaw()=>new(Tool,ParseRaw(ArgumentsJson),ParseRaw(ResultJson),Evidence,CreatedAt,ExecutionId);
    private static JsonElement ParseRaw(string json){using var document=JsonDocument.Parse(json);return document.RootElement.Clone();}
}

internal sealed record DreamFrozenCall(string Id,string Name,string ArgumentsJson)
{
    public static DreamFrozenCall Freeze(DreamToolCall call)=>new(call.Id,call.Name,call.Arguments.GetRawText());
    public DreamToolCall Thaw(){using var args=JsonDocument.Parse(ArgumentsJson);return new(Id,Name,args.RootElement.Clone());}
}
internal sealed record DreamFrozenMessage(string Role,string? Content=null,IReadOnlyList<DreamFrozenCall>? ToolCalls=null,string? ToolCallId=null)
{
    public static DreamFrozenMessage Freeze(DreamModelMessage message)=>new(message.Role,message.Content,message.ToolCalls?.Select(DreamFrozenCall.Freeze).ToArray(),message.ToolCallId);
    public DreamModelMessage Thaw()=>new(Role,Content,ToolCalls?.Select(call=>call.Thaw()).ToArray(),ToolCallId);
}
internal sealed record DreamFrozenPending(Guid ExecutionId,Guid OriginRunId,DreamFrozenCall Call);
