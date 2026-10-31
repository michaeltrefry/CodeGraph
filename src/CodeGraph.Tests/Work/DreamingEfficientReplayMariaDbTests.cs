using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
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
