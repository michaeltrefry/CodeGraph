using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService
{
    private async Task<object> ExecuteDreamerWorkAsync(string tool,JsonElement arguments,WorkActor actor,CancellationToken ct)
    {
        var scope=DreamPolicy.RequireScope(actor);
        if(arguments.TryGetProperty("session_id",out _)) DreamPolicy.Refuse(actor,"use a different session");
        if(tool==WorkTools.Record)
        {
            var request=WorkJson.Parse<RecordRequest>(arguments,tool);
            if(request.Retract is not null||request.Acknowledge is not null) DreamPolicy.Refuse(actor,"retract or acknowledge knowledge");
            return await RecordAsync(request,actor,null,ct);
        }
        if(tool==WorkTools.Promote)
        {
            var request=WorkJson.Parse<PromoteRequest>(arguments,tool);
            if(request.Action!=PromoteAction.Propose) DreamPolicy.Refuse(actor,"approve, reject or address rules");
            return await PromoteAsync(request,actor,null,ct);
        }
        if(tool==WorkTools.Challenge)
        {
            var request=WorkJson.Parse<ChallengeRequest>(arguments,tool);
            if(request.Resolve is not null) DreamPolicy.Refuse(actor,"resolve challenges");
            return await ChallengeAsync(request,actor,null,ct);
        }
        if(tool==WorkTools.Recall) return await RecallAsync(WorkJson.Parse<RecallRequest>(arguments,tool),null,ct);
        if(tool==WorkTools.Lessons) return await LessonsAsync(WorkJson.Parse<LessonsRequest>(arguments,tool),null,ct);
        if(tool==WorkTools.ReviewQueue) return await ReviewQueueAsync(WorkJson.Parse<ReviewQueueRequest>(arguments,tool),null,ct);
        // Proposal creation is added by the proposal service; it remains the only additional permitted write.
        if(tool=="dream_propose") return await RunDreamProposalAsync(arguments,actor,ct);
        throw new WorkException(WorkErrorCodes.Forbidden,$"The dreamer may not invoke {tool}. Nothing was changed.");
    }
    private Task<object> RunDreamProposalAsync(JsonElement arguments,WorkActor actor,CancellationToken ct) =>
        throw new WorkException(WorkErrorCodes.Forbidden,"Dream proposals are unavailable in this host.");
}
