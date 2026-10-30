using System.Text.Json;
using CodeGraph.Models.Work;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService
{
    private async Task<object> ExecuteDreamSurfaceAsync(string tool,JsonElement args,WorkActor actor,CancellationToken ct)
    {
        DreamPolicy.Refuse(actor,"control dreaming runs");
        var dreaming=serviceProvider?.GetService<IDreamingService>()??throw new WorkStoreUnavailableException();
        if(tool==WorkTools.DreamRun)
        {var r=WorkJson.Parse<DreamRunRequest>(args,tool);var p=await RequireProjectAsync(r.Project,"project",ct);return await dreaming.RunAsync(p.Id,r.Trigger,actor,ct);}
        if(tool==WorkTools.DreamRuns)
        {
            var r=WorkJson.Parse<DreamRunsRequest>(args,tool);var p=await RequireProjectAsync(r.Project,"project",ct);
            if(r.Id is {} id) return await dreaming.GetRunAsync(p.Id,ParseDreamId(id),ct)??throw WorkException.NotFound("No such dreaming run in this project.");
            if(r.Limit is <1 or >100) throw WorkException.Invalid("limit must be 1 to 100.");
            return await dreaming.ListRunsBeforeAsync(p.Id,r.Before is {} before?ParseDreamId(before):null,r.Limit,ct);
        }
        if(tool==WorkTools.DreamSettings)
        {var r=WorkJson.Parse<DreamSettingsRequest>(args,tool);if(r.Settings is {} settings) await dreaming.SetSettingsAsync(settings,actor,ct);return await dreaming.GetSettingsAsync(ct);}
        if(tool==WorkTools.DreamInstructions)
        {
            var r=WorkJson.Parse<DreamInstructionsRequest>(args,tool);var p=await RequireProjectAsync(r.Project,"project",ct);
            if(r.Content is {} content)
            {
                if(r.Scope is null||r.ExpectedRevision is null)throw WorkException.Invalid("Editing instructions requires scope and expected_revision.");
                await dreaming.SetInstructionsAsync(p.Id,r.Scope,content,r.ExpectedRevision.Value,actor,ct);
            }
            else if(r.Scope is not null||r.ExpectedRevision is not null)throw WorkException.Invalid("scope and expected_revision go with content.");
            return await dreaming.GetInstructionsAsync(p.Id,ct);
        }
        if(tool==WorkTools.DreamRevert)
        {var r=WorkJson.Parse<DreamRevertRequest>(args,tool);var p=await RequireProjectAsync(r.Project,"project",ct);return await serviceProvider!.GetRequiredService<IKnowledgeDreamActions>().RevertAsync(p.Id,ParseDreamId(r.Id),actor,ct);}
        throw WorkException.Invalid("Unknown dreaming operation.");
    }
    private static Guid ParseDreamId(string id)=>Guid.TryParse(id,out var value)?value:throw WorkException.Invalid("The dreaming run id must be a GUID.");
}
