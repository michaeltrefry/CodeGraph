using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Counted_large_utf8_source_replay_verifies_through_real_policy_and_persists_its_journal()
    {
        var (project, entry) = await StaleFactAsync();
        var source = string.Concat(Enumerable.Repeat("// café 界 evidence\n", 10000)) +
            "public static class Source { public static int Answer() => 42; }";
        await File.WriteAllTextAsync(Path.Combine(root, "codegraph", "Source.cs"), source);
        await graph.UpsertFileHashBatchAsync("codegraph", new Dictionary<string, string> { ["Source.cs"] = Hash(source) });
        DreamModelResponse[] Responses() => [
            new(null, [Call("read", "dream_read_source", new { repository = "codegraph", path = "Source.cs" })], 7000, 100),
            new(null, [Call("verify", "dream_verify", Verify(entry))], 30000, 100),
            new("Verified the original fact against its current source", [], 30000, 20)];

        // Same real source and policy, original byte-only admission: no mutation is possible.
        var byteOnly = new ScriptedModel(Responses());
        var refused = await (await ServiceAsync(byteOnly)).RunAsync(project, "schedule", Human);
        refused.Status.ShouldBe("budget_exhausted");
        refused.Reason.ShouldBe("input_budget");
        byteOnly.Requests.Count.ShouldBe(1);
        refused.ActionCounts.ShouldBeEmpty();
        (await db.Store.LastDreamJournalIdAsync(project, refused.Id)).ShouldBeNull();

        var counted = new ScriptedModel(Responses()) { InputCount = 30000 };
        var completed = await (await ServiceAsync(counted)).RunAsync(project, "schedule", Human);

        completed.Status.ShouldBe("completed", completed.Reason);
        counted.CountRequests.ShouldBe(3);
        counted.Requests.Count.ShouldBe(3);
        counted.Requests.ShouldAllBe(request => request.MaxTokens == 8192);
        SuccessfulWrites(completed, 2);
        completed.ActionCounts["dream_verify"].ShouldBe(1);
        (completed.InputTokens + completed.OutputTokens).ShouldBe(67220);
        var current = (await db.Store.GetKnowledgeAsync(entry.Id, true))!;
        current.Version.ShouldBe(entry.Version + 1);
        current.PossiblyStale.ShouldBeFalse();
        var journal = await db.Store.LastDreamJournalIdAsync(project, completed.Id);
        journal.ShouldNotBeNull();
        await using var connection = new MySqlConnection(db.ConnectionString);
        var durable = await connection.QuerySingleAsync<(string Action, string Evidence, string Prevalence)>(
            "SELECT action,evidence_json,prevalence FROM dream_write_journal WHERE id=@journal AND run_id=@run AND project_id=@project AND entry_id=@entry",
            new { journal, run = completed.Id, project, entry = entry.Id });
        durable.Action.ShouldBe("verified");
        durable.Evidence.ShouldContain(entry.Id.ToString());
        durable.Prevalence.ShouldBe("one entry");
        (await runs.GetRunAsync(project, completed.Id))!.ActionCounts["dream_verify"].ShouldBe(1);
    }
}
