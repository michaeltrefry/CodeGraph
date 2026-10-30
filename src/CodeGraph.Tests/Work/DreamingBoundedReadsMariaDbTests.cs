using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Large_valid_history_starts_the_model_within_budget_and_every_entry_and_full_revision_remains_reachable()
    {
        var project = await ProjectAsync();
        var foreign = await ProjectAsync("inference");
        await RecordAsync(foreign, "Foreign secret never belongs in a dream read");
        var entries = new List<KnowledgeView>();
        for (var index = 0; index < 102; index++)
            entries.Add((await ((IKnowledgeStore)db.Store).RecordKnowledgeAsync(project,
                new(KnowledgeKind.Fact, $"Fact {index:D3}", $"Body {index:D3}:" + new string(index < 2 ? '界' : 'x', 15_990), KnowledgeConfidence.High, null, null, null, [], [], null), Human, null)).Entry);
        // Revisions of one entry, not only creation snapshots, must also remain reachable.
        await db.CallAsync(WorkTools.Record, new { revise = new { id = entries[0].Id, expected_version = entries[0].Version, body = "A revised body:" + new string('y', 15_980) } });
        await using var connection = new MySqlConnection(db.ConnectionString);
        var expected = (await connection.QueryAsync<(string Id, string Snapshot)>(
            "SELECT CONCAT(r.entry_id,':',r.revision) id,r.snapshot_json snapshot FROM knowledge_revisions r JOIN knowledge_entries e ON e.id=r.entry_id WHERE e.project_id=@project", new { project }))
            .ToDictionary(row => row.Id, row => JsonSerializer.Deserialize<JsonElement>(row.Snapshot));
        expected.Count.ShouldBe(103);
        expected.Values.Sum(value => Encoding.UTF8.GetByteCount(value.GetRawText())).ShouldBeGreaterThan(1_000_000);
        var turn = 0;
        var model = new ScriptedModel((request, _) =>
        {
            // These are actual calls through DreamingService's production input reservation,
            // with a real configured 128k budget, not a substitute action/model budget path.
            request.MaxTokens.ShouldBeGreaterThan(0);
            request.MaxTokens.ShouldBeLessThanOrEqualTo(8192);
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)).ShouldBeLessThan(128_000);
            if (++turn == 1)
            {
                request.Messages[0].Content!.Length.ShouldBeLessThan(20_000);
                request.Messages[0].Content!.ShouldNotContain("Body 000:");
                request.Messages[0].Content!.ShouldContain("changed_revisions_count");
                return Task.FromResult(Response(Call("history", "dream_work_trail", new { })));
            }
            var tool = request.Messages.Single(message => message.ToolCallId == "history");
            Encoding.UTF8.GetByteCount(tool.Content!).ShouldBeLessThan(12_100);
            return Task.FromResult(Response());
        });
        var run = await (await ServiceAsync(model, maxTokens: 128_000)).RunAsync(project, "run_now", Human);
        run.Status.ShouldBe("completed", run.Reason);
        model.Requests.Count.ShouldBe(2);

        var actions = (IKnowledgeDreamActions)db.Service;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var firstHistory = await ReadBoundedAsync(actions, "dream_work_trail", new { limit = 100 });
        var firstEntries = await ReadBoundedAsync(actions, "dream_read", new { detail = "full", limit = 100 });
        firstHistory.GetProperty("total_count").GetInt32().ShouldBe(expected.Count);
        firstEntries.GetProperty("total_count").GetInt32().ShouldBe(entries.Count);
        var cursor = firstHistory.GetProperty("next_cursor").GetString()!;
        (await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_read", JsonSerializer.SerializeToElement(new { cursor }), scope.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        using (var otherProject = DreamExecutionScope.Begin(Guid.NewGuid(), foreign, "scripted"))
            (await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_work_trail", JsonSerializer.SerializeToElement(new { cursor }), otherProject.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        using (var otherRun = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted"))
            (await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_work_trail", JsonSerializer.SerializeToElement(new { cursor }), otherRun.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // Writes after a page is returned cannot move its membership/order or replace an unread
        // version. The lower watermark still leaves both changes eligible for the following run.
        db.Clock.Advance(TimeSpan.FromSeconds(1));
        Task changes;
        using (ExecutionContext.SuppressFlow()) changes = Task.Run(async () =>
        {
            var current = (await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(entries[1].Id, false))!;
            await db.CallAsync(WorkTools.Record, new { revise = new { id = current.Id, expected_version = current.Version, body = "Changed while pages were in flight" } });
            await RecordAsync(project, "New entry after the read window");
        });
        await changes;

        var history = await FinishBoundedReadAsync(actions, "dream_work_trail", firstHistory);
        history.Count.ShouldBe(expected.Count);
        history.Select(row => row.GetProperty("id").GetString()).Distinct().Count().ShouldBe(expected.Count);
        foreach (var row in history)
        {
            row.GetProperty("kind").GetString().ShouldBe("revision");
            JsonElement.DeepEquals(row.GetProperty("data"), expected[row.GetProperty("id").GetString()!]).ShouldBeTrue();
            row.GetRawText().ShouldNotContain("Foreign secret");
        }
        var knowledge = await FinishBoundedReadAsync(actions, "dream_read", firstEntries);
        knowledge.Count.ShouldBe(entries.Count);
        knowledge.Select(row => row.GetProperty("id").GetGuid()).Order().ShouldBe(entries.Select(entry => entry.Id).Order());
        knowledge.ShouldNotContain(row => row.GetRawText().Contains("Changed while pages were in flight"));
        knowledge.ShouldNotContain(row => row.GetRawText().Contains("Foreign secret"));
        var latest = await ReadBoundedAsync(actions, "dream_work_trail", new { });
        latest.GetProperty("total_count").GetInt32().ShouldBe(expected.Count + 2);
    }

    [Fact]
    public async Task Lesson_pages_keep_their_captured_ranking_when_a_later_write_changes_membership()
    {
        var project = await ProjectAsync();
        var lessons = new List<KnowledgeView>();
        for (var index = 0; index < 6; index++)
            lessons.Add(await LessonAsync(project, $"Prompt rule {index} keeps its own specific condition", CorrectionSourceKind.Prompt, Human));
        var actions = (IKnowledgeDreamActions)db.Service;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var first = await ReadBoundedAsync(actions, "dream_lessons", new { view = "unaddressed", limit = 1 });
        first.GetProperty("total_count").GetInt32().ShouldBe(6);
        var returned = first.GetProperty("items")[0].GetProperty("id").GetGuid();
        var changed = lessons.First(lesson => lesson.Id != returned);
        Task mutation;
        using (ExecutionContext.SuppressFlow()) mutation = Task.Run(() => db.CallAsync(WorkTools.Record,
            new { retract = new { id = changed.Id, expected_version = changed.Version, reason = "A different request retires this rule during pagination" } }));
        await mutation;
        var captured = await FinishBoundedReadAsync(actions, "dream_lessons", first);
        captured.Select(row => row.GetProperty("id").GetGuid()).Order().ShouldBe(lessons.Select(lesson => lesson.Id).Order());
        var current = await ReadBoundedAsync(actions, "dream_lessons", new { view = "unaddressed" });
        current.GetProperty("total_count").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Dream_review_pages_reach_past_the_interactive_500_candidate_bound()
    {
        var project = await ProjectAsync();
        for (var index = 0; index < 503; index++)
            await db.CallAsync(WorkTools.Record, new { project, entry = new { kind = "decision", summary = $"Autonomous decision {index:D3}", made_autonomously = true } });
        var actions = (IKnowledgeDreamActions)db.Service;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var page = await ReadBoundedAsync(actions, "dream_review_queue", new { limit = 100 });
        page.GetProperty("total_count").GetInt32().ShouldBe(503);
        var rows = await FinishBoundedReadAsync(actions, "dream_review_queue", page);
        rows.Select(row => row.GetProperty("entry").GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(503);
    }

    [Fact]
    public async Task A_mutable_trail_row_changed_after_capture_returns_conflict_instead_of_mixing_json_fragments()
    {
        var project = await ProjectAsync();
        var item = await db.NewItemAsync(project.ToString(), "Mutable evidence in an otherwise immutable read window");
        await db.CallAsync(WorkTools.Post, new { project, item = item.GetProperty("key").GetString(), body = "Original project message", kind = "info" });
        // A single reference is enough to test the deferred-read version guard without reading
        // and caching its body first. Production snapshots retain these same keys/hashes.
        var store = (IDreamingKnowledgeStore)db.Store;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var reference = (await store.ListDreamWorkTrailAsync(project, null)).Single(row => row.Kind == "message");
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE work_messages SET body='A different request changed this message' WHERE id=@id", new { id = reference.Id });
        (await Should.ThrowAsync<WorkException>(() => store.ReadDreamWorkTrailItemAsync(project, reference))).Code.ShouldBe(WorkErrorCodes.Conflict);
        var fresh = (await store.ListDreamWorkTrailAsync(project, null)).Single(row => row.Kind == "message");
        (await store.ReadDreamWorkTrailItemAsync(project, fresh)).GetProperty("data").GetProperty("body").GetString().ShouldBe("A different request changed this message");
    }

    private static async Task<JsonElement> ReadBoundedAsync(IKnowledgeDreamActions actions, string tool, object arguments)
    {
        var wire = JsonSerializer.SerializeToElement(await actions.ExecuteToolAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), DreamExecutionScope.Current!.Actor), WorkJson.Options);
        Encoding.UTF8.GetByteCount(wire.GetRawText()).ShouldBeLessThan(12_100);
        var data = wire.GetProperty("data");
        data.GetProperty("has_more").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        return data;
    }

    private static async Task<List<JsonElement>> FinishBoundedReadAsync(IKnowledgeDreamActions actions, string tool, JsonElement page)
    {
        var result = new List<JsonElement>();
        var fragmentText = new StringBuilder();
        var total = page.GetProperty("total_count").GetInt32();
        var captured = page.GetProperty("snapshot_at").GetDateTime();
        var calls = 0;
        while (true)
        {
            (++calls).ShouldBeLessThan(1000);
            page.GetProperty("total_count").GetInt32().ShouldBe(total);
            page.GetProperty("snapshot_at").GetDateTime().ShouldBe(captured);
            result.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
            if (page.TryGetProperty("fragment", out var fragment))
            {
                fragment.GetProperty("offset_chars").GetInt32().ShouldBe(fragmentText.Length);
                fragmentText.Append(fragment.GetProperty("content").GetString());
                if (!fragment.GetProperty("has_more").GetBoolean())
                {
                    fragmentText.Length.ShouldBe(fragment.GetProperty("total_chars").GetInt32());
                    result.Add(JsonSerializer.Deserialize<JsonElement>(fragmentText.ToString()));
                    fragmentText.Clear();
                }
            }
            if (!page.GetProperty("has_more").GetBoolean()) break;
            page = await ReadBoundedAsync(actions, tool, new { cursor = page.GetProperty("next_cursor").GetString(), limit = 100 });
        }
        fragmentText.Length.ShouldBe(0);
        result.Count.ShouldBe(total);
        return result;
    }
}
