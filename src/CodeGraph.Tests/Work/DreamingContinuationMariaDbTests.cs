using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Exhausted_attempt_resumes_visible_evidence_without_repeating_reads_and_retires_its_checkpoint()
    {
        var project=await ProjectAsync();
        var entry=await RecordAsync(project,"Checkpoint evidence remains available to the next scheduled attempt");
        var firstModel=new ScriptedModel(new DreamModelResponse(null,[Call("remember","dream_read",new {id=entry.Id})],121000,1000)) {InputCount=1000};
        var first=await (await ServiceAsync(firstModel)).RunAsync(project,"schedule",Human);
        first.Status.ShouldBe("budget_exhausted",first.Reason);
        first.ContinuationAvailable.ShouldBeTrue();first.Actions.Count.ShouldBe(1);firstModel.Requests.Count.ShouldBe(1);
        (first.InputTokens+first.OutputTokens).ShouldBe(122000);
        var original=(await runs.GetCheckpointAsync(project))!;
        original.RunId.ShouldBe(first.Id);
        db.Clock.Advance(TimeSpan.FromHours(1));
        var nextModel=new ScriptedModel((request,_)=>
        {
            request.System.ShouldContain("remaining: 128000");
            request.System.ShouldContain("continuation of earlier attempts");
            var evidence=request.Messages.Single(m=>m.ToolCallId=="remember").Content!;
            evidence.ShouldContain(entry.Id.ToString());evidence.ShouldContain("Checkpoint evidence remains available");
            return Task.FromResult(new DreamModelResponse("Completed the selected evidence check; no changes were needed.",[],1000,50));
        }) {InputCount=1000};
        var next=await (await ServiceAsync(nextModel)).RunAsync(project,"schedule",Human);
        next.Status.ShouldBe("completed",next.Reason);next.ContinuedFrom.ShouldBe(first.Id);
        next.SweepStartedAt.ShouldBe(first.SweepStartedAt);next.InputTokens.ShouldBe(1000);next.OutputTokens.ShouldBe(50);
        next.Actions.ShouldBeEmpty();nextModel.Requests.Count.ShouldBe(1);next.ContinuationAvailable.ShouldBeFalse();
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
        var prior=(await runs.GetRunAsync(project,first.Id))!;
        prior.Status.ShouldBe("budget_exhausted");prior.Actions.Single().Result.GetRawText().ShouldBe(first.Actions[0].Result.GetRawText());
        (await runs.GetSettingsAsync()).MaxTokensPerRun.ShouldBe(128000);
        (await runs.GetSettingsAsync()).MaxTurns.ShouldBe(12);
    }

    [Fact]
    public async Task Unsupported_checkpoint_is_not_replaced_and_an_oversized_attempt_never_generates_or_loses_progress()
    {
        var project=await ProjectAsync();await RecordAsync(project,"Signal");
        var model=new ScriptedModel() {InputCount=1000000};var service=await ServiceAsync(model);
        var first=await service.RunAsync(project,"schedule",Human);
        first.Status.ShouldBe("budget_exhausted");first.InputTokens.ShouldBe(0);model.Requests.ShouldBeEmpty();
        var old=(await runs.GetCheckpointAsync(project))!;
        var second=await service.RunAsync(project,"schedule",Human);
        second.Status.ShouldBe("budget_exhausted");second.Reason.ShouldBe("continuation_input_budget");second.Report!.ShouldContain("no generation was sent");second.ContinuedFrom.ShouldBe(first.Id);second.InputTokens.ShouldBe(0);
        model.Requests.ShouldBeEmpty();(await runs.GetCheckpointAsync(project))!.RunId.ShouldBe(second.Id);
        var invalid=JsonSerializer.SerializeToElement(new {version=999,private_marker="retain me"});
        await runs.SaveProgressAsync(second,invalid,second.Id);
        var failed=await service.RunAsync(project,"schedule",Human);
        failed.Status.ShouldBe("failed");failed.ContinuationAvailable.ShouldBeTrue();model.Requests.ShouldBeEmpty();
        var retained=(await runs.GetCheckpointAsync(project))!;
        retained.RunId.ShouldBe(second.Id);retained.State.GetProperty("private_marker").GetString().ShouldBe("retain me");
    }
    [Fact]
    public async Task Malformed_call_refusal_survives_exhaustion_and_does_not_poison_or_repeat_on_resume()
    {
        var project=await ProjectAsync();var entry=await RecordAsync(project,"Independent valid read");
        using var duplicate=JsonDocument.Parse("{\"detail\":\"summary\",\"detail\":\"full\"}");
        var invalid=new DreamToolCall("duplicate","dream_read",duplicate.RootElement.Clone());
        var firstModel=new ScriptedModel(new DreamModelResponse(null,[invalid,Call("valid","dream_read",new {id=entry.Id})],121000,1000)) {InputCount=1000};
        var first=await (await ServiceAsync(firstModel)).RunAsync(project,"schedule",Human);
        first.Status.ShouldBe("budget_exhausted",first.Reason);first.Actions.Count.ShouldBe(2);
        first.Actions[0].Result.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.InvalidArgument);
        var nextModel=new ScriptedModel((request,_)=>
        {
            request.Messages.Single(m=>m.ToolCallId=="duplicate").Content!.ShouldContain("invalid_argument");
            request.Messages.Single(m=>m.ToolCallId=="valid").Content!.ShouldContain(entry.Id.ToString());
            return Task.FromResult(new DreamModelResponse("The malformed request was refused; the independent evidence was read.",[],1000,50));
        }) {InputCount=1000};
        var next=await (await ServiceAsync(nextModel)).RunAsync(project,"schedule",Human);
        next.Status.ShouldBe("completed",next.Reason);next.Actions.ShouldBeEmpty();
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_checkpoint_owner_cannot_write_or_propose_even_if_its_running_row_survives(bool successorCompleted)
    {
        var project=await ProjectAsync();var evidence=await RecordAsync(project,"Fencing evidence");
        var first=ContinuationRun(project) with {ContinuationAvailable=true};
        await runs.SaveProgressAsync(first,JsonSerializer.SerializeToElement(new {marker="first"}),null);
        var successor=ContinuationRun(project) with {ContinuationAvailable=!successorCompleted,Status=successorCompleted?"completed":"running"};
        await runs.SaveProgressAsync(successor,successorCompleted?null:JsonSerializer.SerializeToElement(new {marker="second"}),first.Id);
        using var scope=DreamExecutionScope.Begin(first.Id,project,first.Model);scope.ExecutionId=Guid.NewGuid();
        var record=JsonSerializer.SerializeToElement(new {entry=new {kind="fact",summary="Must not be written",confidence="high"},evidence=Evidence(evidence),prevalence="one anchor"},WorkJson.Options);
        var refused=await Should.ThrowAsync<WorkException>(()=>((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_record",record,scope.Actor));
        refused.Code.ShouldBe(WorkErrorCodes.Forbidden);
        var proposal=JsonSerializer.SerializeToElement(new {action="retract",target=evidence.Id,expected_version=evidence.Version,rationale="Must not be proposed",prevalence="one anchor",evidence=new[]{new {kind="entry",id=evidence.Id.ToString()}}},WorkJson.Options);
        (await Should.ThrowAsync<WorkException>(()=>((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_propose",proposal,scope.Actor))).Code.ShouldBe(WorkErrorCodes.Forbidden);
        (await db.Store.LastDreamJournalIdAsync(project,first.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_zero_usage_response_does_not_claim_that_no_generation_was_sent()
    {
        var project=await ProjectAsync();var entry=await RecordAsync(project,"Zero reported usage still has a dispatched generation");
        var old=ContinuationRun(project);await runs.SaveRunAsync(old);
        using(var scope=DreamExecutionScope.Begin(old.Id,project,old.Model))
            await SaveRecoveryAsync(old,await RecoveryStateAsync(old));
        var model=new ScriptedModel(new DreamModelResponse(null,[Call("zero","dream_read",new {id=entry.Id})],0,0))
            {InputCounter=request=>request.Messages.Any(m=>m.ToolCallId=="zero")?1000000:1000};
        var run=await(await ServiceAsync(model)).RunAsync(project,"schedule",Human);
        run.Status.ShouldBe("budget_exhausted",run.Reason);run.Reason.ShouldBe("input_budget");model.Requests.Count.ShouldBe(1);
        run.InputTokens.ShouldBe(0);run.Report.ShouldBeNull();
    }

}
