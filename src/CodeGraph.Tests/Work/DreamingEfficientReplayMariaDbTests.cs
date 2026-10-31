using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Theory]
    [InlineData("fact")]
    [InlineData("lesson")]
    public async Task Empty_domains_are_omitted_then_real_records_restore_reads_and_consumed_trail_is_not_replayed(string kind)
    {
        var project=await ProjectAsync();
        var body="Exact message evidence: "+new string('x',6000);
        await db.CallAsync(WorkTools.Post,new {project,kind="info",body});
        var turn=0;
        string? beforeWrite=null;
        var model=new ScriptedModel((request,cancellationToken)=>
        {
            turn++;
            var names=request.Tools.Select(t=>t.Name).ToArray();
            if(turn==1)
            {
                names.ShouldNotContain("dream_read");
                names.ShouldNotContain("dream_lessons");
                names.ShouldNotContain("dream_similar");
                names.ShouldNotContain("dream_review_queue");
                names.ShouldContain("dream_work_trail");
                names.ShouldContain("dream_read_source");
                return Task.FromResult(Response(Call("trail","dream_work_trail",new {kind="message",detail="full"})));
            }
            if(turn==2)
            {
                beforeWrite=request.Messages.Single(m=>m.ToolCallId=="trail").Content!;
                var row=JsonDocument.Parse(beforeWrite).RootElement.GetProperty("data").GetProperty("items")[0];
                row.GetProperty("data").GetProperty("body").GetString().ShouldBe(body);
                var entry=new Dictionary<string,object> { ["kind"]=kind,["summary"]="A grounded observation from one message",["confidence"]="medium" };
                if(kind=="lesson")
                {
                    entry["what"]="Check the observed condition";
                    entry["source"]=new {kind="agent"};
                    entry["applies_when"]=new {activity=new[]{"testing"}};
                }
                return Task.FromResult(Response(Call("record","dream_record",new {entry,
                    evidence=new[]{new {kind="message",reference=row.GetProperty("id").GetString(),summary="The exact message was read this run"}},
                    prevalence="One observed project message; wider recurrence unavailable"})));
            }
            var recordResult=request.Messages.Single(m=>m.ToolCallId=="record").Content!;
            JsonDocument.Parse(recordResult).RootElement.TryGetProperty("error",out _).ShouldBeFalse(recordResult);
            names.ShouldContain("dream_read");
            names.ShouldContain("dream_review_queue");
            names.Contains("dream_lessons").ShouldBe(kind=="lesson");
            names.ShouldNotContain("dream_similar");
            var receipt=request.Messages.Single(m=>m.ToolCallId=="trail").Content!;
            receipt.Length.ShouldBeLessThan(beforeWrite!.Length/2);
            var compact=JsonDocument.Parse(receipt).RootElement.GetProperty("data").GetProperty("items")[0];
            compact.TryGetProperty("data",out _).ShouldBeFalse();
            compact.GetProperty("compacted_after").GetProperty("journal_id").GetInt64().ShouldBeGreaterThan(0);
            return Task.FromResult(new DreamModelResponse("Recorded the selected evidence; other work was not inspected.",[],1000,40));
        }) {InputCount=10000};
        var run=await (await ServiceAsync(model)).RunAsync(project,"schedule",Human);
        run.Status.ShouldBe("completed",run.Reason);
        turn.ShouldBe(3);
        run.Actions.Last().Result.TryGetProperty("error",out _).ShouldBeFalse(run.Actions.Last().Result.GetRawText());
        (await runs.GetRunAsync(project,run.Id))!.Actions.First().Result.GetProperty("data").GetProperty("items")[0]
            .GetProperty("data").GetProperty("body").GetString().ShouldBe(body);
        (await db.Store.LastDreamJournalIdAsync(project,run.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Full_source_is_seen_before_real_verification_but_not_recharged_in_the_final_report_replay()
    {
        var (project, entry) = await StaleFactAsync();
        var source = string.Concat(Enumerable.Repeat("// café 界 source evidence\n", 4000)) + "public static int Answer() => 42;";
        await File.WriteAllTextAsync(Path.Combine(root, "codegraph", "Source.cs"), source);
        await graph.UpsertFileHashBatchAsync("codegraph", new Dictionary<string, string> { ["Source.cs"] = Hash(source) });
        var model = new ScriptedModel(
            new(null, [Call("source", "dream_read_source", new { repository = "codegraph", path = "Source.cs" })], 7000, 100),
            new(null, [Call("verify", "dream_verify", Verify(entry))], 30000, 100),
            new("Verified the selected fact from its source; unrelated history was not inspected.", [], 3000, 40)) { InputCount = 30000 };

        var run = await (await ServiceAsync(model)).RunAsync(project, "schedule", Human);

        run.Status.ShouldBe("completed", run.Reason);
        run.ActionCounts["dream_verify"].ShouldBe(1);
        var beforeWrite = model.Requests[1].Messages.Single(m => m.ToolCallId == "source").Content!;
        JsonDocument.Parse(beforeWrite).RootElement.GetProperty("untrusted_data").GetProperty("source").GetString().ShouldBe(source);
        var afterWrite = model.Requests[2].Messages.Single(m => m.ToolCallId == "source").Content!;
        afterWrite.Length.ShouldBeLessThan(beforeWrite.Length / 20);
        JsonDocument.Parse(afterWrite).RootElement.GetProperty("untrusted_data").GetProperty("compacted_after")
            .GetProperty("journal_id").GetInt64().ShouldBeGreaterThan(0);
        model.Requests[2].System.ShouldContain("already charged: 37200");
        model.Requests[2].System.ShouldContain("remaining: 90800");
        var final = model.Requests[2];
        var uncompressed = final.Messages.Select(m => m.ToolCallId == "source" ? m with { Content = beforeWrite } : m).ToArray();
        var oldReserve = DreamingService.ConservativeInputBudget(final.System, uncompressed, final.Tools);
        var newReserve = DreamingService.ConservativeInputBudget(final.System, final.Messages, final.Tools);
        oldReserve.ShouldBeGreaterThan(90800);
        newReserve.ShouldBeLessThan(90800);
        (run.InputTokens + run.OutputTokens).ShouldBe(40240);
        var durable = (await runs.GetRunAsync(project, run.Id))!;
        durable.Actions.Single(a => a.Tool == "dream_read_source").Result.GetProperty("untrusted_data").GetProperty("source").GetString().ShouldBe(source);
        (await db.Store.GetKnowledgeAsync(entry.Id, true))!.PossiblyStale.ShouldBeFalse();
        (await db.Store.LastDreamJournalIdAsync(project, run.Id)).ShouldNotBeNull();
    }
}
