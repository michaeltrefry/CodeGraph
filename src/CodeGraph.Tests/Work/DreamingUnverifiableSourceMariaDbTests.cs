using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unverifiable_source_is_explicit_metadata_until_full_inspection_and_never_bypasses_hash_policy(bool missingIndex)
    {
        var (project,entry)=await StaleFactAsync();
        var runId=Guid.NewGuid();
        await runs.SaveRunAsync(new(runId,project,"schedule",Human,"running",db.Clock.GetUtcNow().UtcDateTime,null,"scripted","scripted",0,0,0,0,0,null,[]));
        using var scope=DreamExecutionScope.Begin(runId,project,"scripted");
        await sources.ExecuteAsync(project,"dream_read_source",JsonSerializer.SerializeToElement(new {repository="codegraph",path="Source.cs"}));
        scope.ReadFileHash("codegraph","Source.cs").ShouldNotBeNull();
        var source="// changed current source\n"+new string('x',40000);
        await File.WriteAllTextAsync(Path.Combine(root,"codegraph","Source.cs"),source);
        if(missingIndex)
        {
            await using var c=new MySqlConnection(db.ConnectionString);
            await c.ExecuteAsync("DELETE FROM file_hashes WHERE repository='codegraph' AND rel_path='Source.cs'");
        }
        async Task<JsonElement> Read(object args)=>JsonSerializer.SerializeToElement(await sources.ExecuteAsync(project,"dream_read_source",JsonSerializer.SerializeToElement(args)),WorkJson.Options).GetProperty("untrusted_data");
        var auto=await Read(new {repository="codegraph",path="Source.cs"});
        auto.GetProperty("verification_ready").GetBoolean().ShouldBeFalse();
        auto.GetProperty("source_omitted").GetBoolean().ShouldBeTrue();
        auto.GetProperty("reason").GetString().ShouldBe(missingIndex?"unindexed_source":"source_index_mismatch");
        auto.TryGetProperty("source",out _).ShouldBeFalse();
        scope.ReadFile("codegraph","Source.cs").ShouldBeFalse();
        auto.GetRawText().Length.ShouldBeLessThan(1500);
        scope.ReadFileHash("codegraph","Source.cs").ShouldBeNull();
        var full=await Read(auto.GetProperty("read_full").GetProperty("arguments"));
        full.GetProperty("source").GetString().ShouldBe(source);
        full.GetProperty("verification_ready").GetBoolean().ShouldBeFalse();
        scope.ReadFileHash("codegraph","Source.cs").ShouldBeNull();
        var refused=await Should.ThrowAsync<WorkException>(()=>((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_verify",JsonSerializer.SerializeToElement(Verify(entry)),scope.Actor));
        refused.Code.ShouldBe(WorkErrorCodes.Forbidden);
        (await db.Store.GetKnowledgeAsync(entry.Id,false))!.Version.ShouldBe(entry.Version);
        // A real index refresh, followed by a fresh complete source read, restores eligibility.
        await graph.UpsertFileHashBatchAsync("codegraph",new Dictionary<string,string>{["Source.cs"]=Hash(source)});
        var current=await Read(new {repository="codegraph",path="Source.cs"});
        current.GetProperty("verification_ready").GetBoolean().ShouldBeTrue();
        current.GetProperty("source").GetString().ShouldBe(source);
        await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_verify",JsonSerializer.SerializeToElement(Verify(entry)),scope.Actor);
        (await db.Store.GetKnowledgeAsync(entry.Id,false))!.Version.ShouldBe(entry.Version+1);
    }

    [Fact]
    public async Task An_unindexed_large_file_can_be_reported_without_loading_unusable_bulk_into_the_next_model_turn()
    {
        var (project,_)=await StaleFactAsync();
        var source="// unindexed build file\n"+new string('x',140000);
        await File.WriteAllTextAsync(Path.Combine(root,"codegraph","Build.sln"),source);
        var model=new ScriptedModel(Response(Call("source","dream_read_source",new {repository="codegraph",path="Build.sln"})),
            new("Build.sln exists but is unindexed; no verification was claimed and no repeated read was attempted.",[],1000,40));
        var run=await (await ServiceAsync(model)).RunAsync(project,"schedule",Human);
        run.Status.ShouldBe("completed",run.Reason);
        run.Actions.Count.ShouldBe(1);
        var next=model.Requests[1];
        var receipt=next.Messages.Single(m=>m.ToolCallId=="source");
        receipt.Content!.ShouldContain("unindexed_source");
        var full=JsonSerializer.Serialize(new {untrusted_data=new {repository="codegraph",path="Build.sln",source}},WorkJson.Options);
        var old=next.Messages.Select(m=>m.ToolCallId=="source"?m with {Content=full}:m).ToArray();
        DreamingService.ConservativeInputBudget(next.System,old,next.Tools).ShouldBeGreaterThan(128000);
        DreamingService.ConservativeInputBudget(next.System,next.Messages,next.Tools).ShouldBeLessThan(128000);
        var durable=(await runs.GetRunAsync(project,run.Id))!;
        durable.Actions.Single().Result.GetProperty("untrusted_data").GetProperty("source_omitted").GetBoolean().ShouldBeTrue();
        using var scope=DreamExecutionScope.Begin(Guid.NewGuid(),project,"scripted");
        var explicitFull=JsonSerializer.SerializeToElement(await sources.ExecuteAsync(project,"dream_read_source",JsonSerializer.SerializeToElement(new {repository="codegraph",path="Build.sln",detail="full"})),WorkJson.Options);
        explicitFull.GetProperty("untrusted_data").GetProperty("source").GetString().ShouldBe(source);
        scope.ReadFileHash("codegraph","Build.sln").ShouldBeNull();
    }
}
