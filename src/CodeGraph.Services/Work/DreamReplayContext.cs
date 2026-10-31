using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>Pressure-only model projection. The immutable action audit is the backing store.</summary>
internal static class DreamReplayContext
{
    internal sealed record ReadInput(int ActionIndex,int OffsetChars=0);
    private static readonly HashSet<string> ReadTools = ["dream_read","dream_lessons","dream_similar","dream_review_queue",
        "dream_work_trail","dream_search_graph","dream_read_source","dream_read_prior"];
    private static readonly string[] MetadataFields = ["id","evidence_id","kind","version","summary","status","rule_status","confidence",
        "possibly_stale","repository","path","node_id","content_hash","indexed_hash","verification_ready","reason","source_omitted",
        "graph_fingerprint","item_id","name","qualified_name","file_path","start_line","end_line","snapshot_at","total_count","offset",
        "has_more","next_cursor","compacted_after","full_result_in_run_report"];

    public static int ArchiveDeliveredReads(List<DreamModelMessage> messages,IReadOnlyList<DreamAction> actions,int deliveredPrefix)
    {
        // The loop appends one tool message per immutable action; call IDs may be reused.
        var actionIndex=0;var saved=0;
        for(var index=0;index<messages.Count;index++)
        {
            var message=messages[index];
            if(message.Role!="tool")continue;
            actionIndex++;
            if(index>=deliveredPrefix||actionIndex>actions.Count||message.Content is null||!ReadTools.Contains(actions[actionIndex-1].Tool))continue;
            using var current=JsonDocument.Parse(message.Content);
            var value=current.RootElement;
            if(value.ValueKind!=JsonValueKind.Object||value.TryGetProperty("error",out _)||value.TryGetProperty("replay_archive",out _))continue;
            if(value.TryGetProperty("untrusted_data",out var source)&&source.ValueKind==JsonValueKind.Object
                &&source.TryGetProperty("compacted_after",out _))continue;
            var receipt=DreamModelContext.ModelJson(JsonSerializer.SerializeToElement(new
            {
                untrusted_data=true,replay_archive=true,previously_delivered=true,metadata_not_evidence=true,
                original_tool=actions[actionIndex-1].Tool,action_index=actionIndex,metadata=Metadata(value),
                full_result_in_run_report=true,
                read=new {tool="dream_read_prior",arguments=new {action_index=actionIndex}},
                note="Previously delivered result archived to fit the input budget. Use earlier analysis or replay the exact immutable result when details matter."
            },WorkJson.Options));
            var difference=Encoding.UTF8.GetByteCount(message.Content)-Encoding.UTF8.GetByteCount(receipt);
            if(difference<=0)continue;
            messages[index]=message with {Content=receipt};saved+=difference;
        }
        return saved;
    }

    // The caller supplies only this active run's audit; no caller-selected project or run lookup.
    public static object ReadPrior(IReadOnlyList<DreamAction> actions,JsonElement arguments)
    {
        var input=WorkJson.Parse<ReadInput>(arguments,"dream_read_prior");
        if(input.ActionIndex<1||input.ActionIndex>actions.Count)throw WorkException.NotFound("No such prior action in this run.");
        var action=actions[input.ActionIndex-1];var json=action.Result.GetRawText();
        if(input.OffsetChars<0||input.OffsetChars>json.Length
            ||input.OffsetChars>0&&input.OffsetChars<json.Length&&char.IsLowSurrogate(json[input.OffsetChars])&&char.IsHighSurrogate(json[input.OffsetChars-1]))
            throw WorkException.Invalid("offset_chars must be a valid character boundary in this prior result; pass next_offset_chars unchanged.");
        object Page(int count)
        {
            var end=input.OffsetChars+count;
            return new {untrusted_data=true,original_tool=action.Tool,action_index=input.ActionIndex,format="json",
                offset_chars=input.OffsetChars,total_chars=json.Length,content=json.Substring(input.OffsetChars,count),
                has_more=end<json.Length,next_offset_chars=end<json.Length?(int?)end:null};
        }
        var low=0;var high=json.Length-input.OffsetChars;
        while(low<high)
        {
            var middle=low+(high-low+1)/2;
            if(JsonSerializer.SerializeToUtf8Bytes(Page(middle),WorkJson.Options).Length<=WorkService.DreamReadPageBytes)low=middle;
            else high=middle-1;
        }
        if(low>0&&input.OffsetChars+low<json.Length&&char.IsHighSurrogate(json[input.OffsetChars+low-1])&&char.IsLowSurrogate(json[input.OffsetChars+low]))low--;
        if(low==0&&input.OffsetChars<json.Length)throw WorkException.Invalid("The replay envelope cannot fit the bounded page.");
        return Page(low);
    }

    private static object Metadata(JsonElement root)
    {
        var data=root.TryGetProperty("untrusted_data",out var source)&&source.ValueKind==JsonValueKind.Object?source
            :root.TryGetProperty("data",out var nested)&&nested.ValueKind==JsonValueKind.Object?nested:root;
        return new {fields=Pick(data),items=data.TryGetProperty("items",out var items)&&items.ValueKind==JsonValueKind.Array
            ?items.EnumerateArray().Select(Pick).ToArray():null,
            node=data.TryGetProperty("node",out var node)&&node.ValueKind==JsonValueKind.Object?Pick(node):null};
    }
    private static Dictionary<string,JsonElement> Pick(JsonElement value) => value.ValueKind==JsonValueKind.Object
        ?value.EnumerateObject().Where(p=>MetadataFields.Contains(p.Name,StringComparer.Ordinal)).ToDictionary(p=>p.Name,p=>p.Value.Clone()) : [];
}
