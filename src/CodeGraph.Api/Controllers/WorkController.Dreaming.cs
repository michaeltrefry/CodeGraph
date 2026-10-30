using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Services.Work;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

public sealed partial class WorkController
{
    [HttpPost("dreaming/{project}/runs")]
    public Task<IActionResult> RunDream(string project)=>DreamBodyAsync(WorkTools.DreamRun,project);
    [HttpGet("dreaming/{project}/runs")]
    public async Task<IActionResult> DreamRuns(string project)
    {
        if(await DeniedAsync(WorkTools.DreamRuns) is {} denied)return denied;
        try
        {var args=QueryArguments(WorkTools.DreamRuns,["before"],["limit"],[]);args["project"]=project;return await RunAsync(WorkTools.DreamRuns,JsonSerializer.SerializeToElement(args));}
        catch(CodeGraph.Models.Work.WorkException ex){return Refuse(ex);}
    }
    [HttpGet("dreaming/{project}/runs/{runId}")]
    public async Task<IActionResult> DreamRun(string project,string runId)
    {if(await DeniedAsync(WorkTools.DreamRuns) is {} denied)return denied;return await RunAsync(WorkTools.DreamRuns,JsonSerializer.SerializeToElement(new {project,id=runId}));}
    [HttpPost("dreaming/{project}/runs/{runId}/revert")]
    public async Task<IActionResult> RevertDreamRun(string project,string runId)
    {if(await DeniedAsync(WorkTools.DreamRevert) is {} denied)return denied;return await RunAsync(WorkTools.DreamRevert,JsonSerializer.SerializeToElement(new {project,id=runId}));}
    [HttpGet("dreaming/{project}/settings")]
    public async Task<IActionResult> DreamSettings(string project)
    {if(await DeniedAsync(WorkTools.DreamSettings) is {} denied)return denied;return await RunAsync(WorkTools.DreamSettings,JsonSerializer.SerializeToElement(new {}));}
    [HttpPut("dreaming/{project}/settings")]
    public async Task<IActionResult> SaveDreamSettings(string project)
    {
        if(await DeniedAsync(WorkTools.DreamSettings) is {} denied)return denied;
        var (body,error)=await ReadBodyAsync(WorkTools.DreamSettings);if(error is not null)return error;
        return await RunAsync(WorkTools.DreamSettings,JsonSerializer.SerializeToElement(new {settings=body}));
    }
    [HttpGet("dreaming/{project}/instructions")]
    public async Task<IActionResult> DreamInstructions(string project)
    {if(await DeniedAsync(WorkTools.DreamInstructions) is {} denied)return denied;return await RunAsync(WorkTools.DreamInstructions,JsonSerializer.SerializeToElement(new {project}));}
    [HttpPut("dreaming/{project}/instructions")]
    public Task<IActionResult> SaveDreamInstructions(string project)=>DreamBodyAsync(WorkTools.DreamInstructions,project);
    private async Task<IActionResult> DreamBodyAsync(string tool,string project)
    {
        if(await DeniedAsync(tool) is {} denied)return denied;
        var (body,error)=await ReadBodyAsync(tool);if(error is not null)return error;
        var args=JsonNode.Parse(body.GetRawText())!.AsObject();
        if(args.ContainsKey("project"))return BadRequest(new {code="invalid_argument",message="project is supplied by the route."});
        args["project"]=project;return await RunAsync(tool,JsonSerializer.SerializeToElement(args));
    }
}
