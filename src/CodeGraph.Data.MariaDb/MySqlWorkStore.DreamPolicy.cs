using System.Text.Json;
using CodeGraph.Models.Work;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    private static void RequireDreamRecord(WorkActor actor,Guid projectId,KnowledgeSpec spec)
    {
        if(!DreamPolicy.IsDreamer(actor)) return;
        DreamPolicy.RequireProject(actor,projectId);DreamPolicy.RequireWrite(actor,"record");
        if(spec.Kind is not (KnowledgeKind.Fact or KnowledgeKind.Gotcha or KnowledgeKind.Constraint or KnowledgeKind.Attempt or KnowledgeKind.Lesson)
           || spec.Supersedes is not null || spec.SameAs is not null || spec.Contradicts is not null
           || spec.Kind==KnowledgeKind.Lesson && spec.Details?.Source?.Kind!=CorrectionSourceKind.Agent)
            throw new WorkException(WorkErrorCodes.Forbidden,"The dreamer may record only new facts, gotchas, constraints, attempts and agent-sourced lessons; retirements require proposals.");
    }
    private async Task RequireDreamRevisionAsync(MySqlConnection connection,MySqlTransaction transaction,Guid id,KnowledgeSpec state,CancellationToken ct)
    {
        var scope=DreamExecutionScope.Current;
        if(scope is null) return;
        var current=(await LoadKnowledgeViewAsync(connection,transaction,id,false,ct))!;
        DreamPolicy.RequireProject(scope.Actor,current.ProjectId);DreamPolicy.RequireWrite(scope.Actor,"revise");
        bool Same<T>(T a,T b)=>JsonSerializer.Serialize(a,WorkJson.Options)==JsonSerializer.Serialize(b,WorkJson.Options);
        if(current.Kind!=KnowledgeKind.Lesson||current.Details?.Source?.Kind==CorrectionSourceKind.Prompt
           || state.Kind!=current.Kind||state.Summary!=current.Summary||state.Body!=current.Body||state.Confidence!=current.Confidence
           || state.MadeAutonomously!=current.MadeAutonomously||!Same(state.Details is {} sd?sd with {AppliesWhen=null}:null,current.Details is {} cd?cd with {AppliesWhen=null}:null)
           ||!Same(state.Repositories??[],current.Repositories??[])||!Same(state.Paths,current.Paths??[])||!Same(state.Nodes,(current.Nodes??[]).Select(n=>n.Id).ToList())
           || state.Supersedes is not null||state.SameAs is not null||state.Contradicts is not null)
            throw new WorkException(WorkErrorCodes.Forbidden,"The dreamer may revise only a non-prompt lesson's applies_when. Nothing was changed.");
    }
}
