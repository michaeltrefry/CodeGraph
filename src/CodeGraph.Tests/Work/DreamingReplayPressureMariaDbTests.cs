using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Input_pressure_archives_only_delivered_reads_recounts_and_finishes_real_verification_with_full_audit_replay()
    {
        var (project, entry) = await StaleFactAsync();
        var source = string.Concat(Enumerable.Repeat("// café 界 😀 source evidence\n", 4000)) + "public static int Answer() => 42;";
        await File.WriteAllTextAsync(Path.Combine(root, "codegraph", "Source.cs"), source);
        await graph.UpsertFileHashBatchAsync("codegraph", new Dictionary<string,string> { ["Source.cs"] = Hash(source) });
        var turn = 0;
        var model = new ScriptedModel((request, cancellationToken) =>
        {
            turn++;
            if (turn == 1)
            {
                request.Tools.Select(t => t.Name).ShouldNotContain("dream_read_prior");
                return Task.FromResult(new DreamModelResponse(null,
                    [Call("source", "dream_read_source", new { repository = "codegraph", path = "Source.cs" })], 9000, 500));
            }
            if (turn == 2)
            {
                request.Tools.Select(t => t.Name).ShouldContain("dream_read_prior");
                JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "source").Content!).RootElement
                    .GetProperty("untrusted_data").GetProperty("source").GetString().ShouldBe(source);
                return Task.FromResult(new DreamModelResponse(null,
                    [Call("entry", "dream_read", new { id = entry.Id, detail = "full" })], 60000, 1000));
            }
            if (turn == 3)
            {
                var receipt = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "source").Content!).RootElement;
                receipt.GetProperty("replay_archive").GetBoolean().ShouldBeTrue();
                receipt.GetProperty("previously_delivered").GetBoolean().ShouldBeTrue();
                receipt.GetProperty("read").GetProperty("arguments").GetProperty("action_index").GetInt32().ShouldBe(1);
                // This read was produced after the last generation: it must reach the model intact.
                var fresh = request.Messages.Single(m => m.ToolCallId == "entry").Content!;
                fresh.ShouldContain(entry.Id.ToString());
                fresh.ShouldNotContain("replay_archive");
                return Task.FromResult(new DreamModelResponse(null,
                    [Call("replay", "dream_read_prior", new { action_index = 1 })], 2000, 100));
            }
            if (turn == 4)
            {
                var replay = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "replay").Content!).RootElement;
                replay.TryGetProperty("error", out _).ShouldBeFalse();
                replay.GetProperty("original_tool").GetString().ShouldBe("dream_read_source");
                replay.GetProperty("has_more").GetBoolean().ShouldBeTrue();
                return Task.FromResult(new DreamModelResponse(null, [Call("verify", "dream_verify", Verify(entry))], 12000, 1000));
            }
            request.Messages.Single(m => m.ToolCallId == "verify").Content!.ShouldContain("journal_id");
            return Task.FromResult(new DreamModelResponse("Verified the selected fact from source; other work was not inspected.", [], 2000, 100));
        })
        {
            InputCounter = request => turn >= 2 && request.Messages.Any(m => m.ToolCallId == "source"
                && m.Content!.Contains("source evidence", StringComparison.Ordinal)) ? 60000 : 1000
        };

        var run = await (await ServiceAsync(model)).RunAsync(project, "schedule", Human);

        run.Status.ShouldBe("completed", run.Reason);
        turn.ShouldBe(5);
        model.CountRequests.ShouldBe(6); // One denied original replay, then the changed request is counted again.
        (run.InputTokens + run.OutputTokens).ShouldBe(87700);
        var durable = (await runs.GetRunAsync(project, run.Id))!;
        durable.Actions.First().Result.GetProperty("untrusted_data").GetProperty("source").GetString().ShouldBe(source);
        (await db.Store.GetKnowledgeAsync(entry.Id, true))!.PossiblyStale.ShouldBeFalse();
        (await db.Store.LastDreamJournalIdAsync(project, run.Id)).ShouldNotBeNull();
        var restored = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var page = JsonSerializer.SerializeToElement(DreamReplayContext.ReadPrior(durable.Actions,
                JsonSerializer.SerializeToElement(new { action_index = 1, offset_chars = offset }, WorkJson.Options)), WorkJson.Options);
            JsonSerializer.SerializeToUtf8Bytes(page, WorkJson.Options).Length.ShouldBeLessThanOrEqualTo(WorkService.DreamReadPageBytes);
            restored.Append(page.GetProperty("content").GetString());
            if (!page.GetProperty("has_more").GetBoolean()) break;
            offset = page.GetProperty("next_offset_chars").GetInt32();
        }
        restored.ToString().ShouldBe(durable.Actions[0].Result.GetRawText());
    }
}
