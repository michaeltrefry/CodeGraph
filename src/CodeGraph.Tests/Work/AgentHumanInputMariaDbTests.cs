using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Agents are never human input (CG-F16 S4): what only a person may do is refused forbidden for an
/// agent and writes nothing, the owner's same calls work as before, and the owner's words relayed
/// by an agent (relayed_prompt, relayed_review) wait in the review queue as proposed lessons, first
/// in line, until the owner approves them; recurrence never promotes them.
/// </summary>
public sealed class AgentHumanInputMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly WorkActor Agent = new("codex-s4", WorkActor.AgentKind);
    private static readonly WorkActor Owner = new("michael");

    private static object Correction(string source) => new
    {
        what = "Answered without checking",
        correct_behavior = "Check the source before answering",
        applies_when = new { activity = new[] { "reviewing" } },
        source = new { kind = source, @ref = Guid.NewGuid().ToString() }
    };

    [Fact]
    public async Task A_persons_correction_or_review_is_refused_on_every_path()
    {
        var project = await db.NewProjectAsync("AH");
        var item = await db.NewItemAsync(project, "Item");
        var key = item.GetProperty("key").GetString()!;
        var session = await StartAsync(project, Agent);

        foreach (var source in new[] { "prompt", "review" })
        {
            await RefusedAsync(WorkTools.Record, new { project, entry = new { kind = "lesson", summary = "Check first", what = "x", applies_when = new { activity = new[] { "reviewing" } }, source = new { kind = source } } });
            await RefusedAsync(WorkTools.UpdateWork, new { id = key, expected_version = item.GetProperty("version").GetInt64(), corrections = new[] { Correction(source) } });
            await RefusedAsync(WorkTools.EndSession, new { session_id = session, corrections = new[] { Correction(source) } });
        }

        var fact = await db.CallAsAsync(WorkTools.Record, new { project, entry = new { kind = "lesson", summary = "Read twice", what = "x", source = new { kind = "agent" } } }, Agent);
        var entry = fact.GetProperty("entry");
        await RefusedAsync(WorkTools.Record, new { project, revise = new { id = entry.GetProperty("id").GetString(), expected_version = entry.GetProperty("version").GetInt64(), source = new { kind = "prompt" } } });

        // Nothing was written: the item, the session and the lesson are as they were.
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64().ShouldBe(item.GetProperty("version").GetInt64());
        (await db.CallAsAsync(WorkTools.FindWork, new { session_id = session, project }, Agent)).ValueKind.ShouldBe(JsonValueKind.Object);
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM knowledge_entries e JOIN work_projects p ON p.id = e.project_id WHERE p.project_key = @project AND e.kind = 'lesson'", new { project })).ShouldBe(1);

        // The owner records a prompt correction as before: a rule at once.
        var owned = await db.CallAsAsync(WorkTools.Record, new { project, entry = new { kind = "lesson", summary = "Owner rule", what = "x", applies_when = new { activity = new[] { "reviewing" } }, source = new { kind = "prompt" } } }, Owner);
        (await RuleStateAsync(owned.GetProperty("entry").GetProperty("id").GetString()!)).ShouldBe("rule");
    }

    [Fact]
    public async Task Approving_reviewing_the_humans_inbox_and_the_dreaming_settings_are_the_owners()
    {
        var project = await db.NewProjectAsync("AI");
        var lesson = (await db.CallAsAsync(WorkTools.Record, new
        {
            project,
            entry = new { kind = "lesson", summary = "Propose me", what = "x", applies_when = new { activity = new[] { "reviewing" } }, source = new { kind = "agent" } }
        }, Agent)).GetProperty("entry");
        var id = lesson.GetProperty("id").GetString()!;
        var version = lesson.GetProperty("version").GetInt64();

        await RefusedAsync(WorkTools.Promote, new { id, expected_version = version, action = "approve" });
        await RefusedAsync(WorkTools.Promote, new { id, expected_version = version, action = "reject", reason = "no" });
        await RefusedAsync(WorkTools.DreamReview, new { project, id = Guid.NewGuid().ToString(), expected_version = 1, action = "accept" });
        await RefusedAsync(WorkTools.DreamReview, new { project, id = Guid.NewGuid().ToString(), expected_version = 1, action = "reject", reason = "no" });
        await RefusedAsync(WorkTools.Inbox, new { project });
        await RefusedAsync(WorkTools.Ack, new { project, up_to = "x" });
        await RefusedAsync(WorkTools.DreamSettings, new { expected_version = "1", settings = new { } });

        // Proposing stays open to agents; the owner approves.
        (await db.CallAsAsync(WorkTools.Promote, new { id, expected_version = version, action = "propose" }, Agent)).ValueKind.ShouldBe(JsonValueKind.Object);
        (await RuleStateAsync(id)).ShouldBe("proposed");
        var current = await VersionAsync(id);
        await db.CallAsAsync(WorkTools.Promote, new { id, expected_version = current, action = "approve" }, Owner);
        (await RuleStateAsync(id)).ShouldBe("rule");
        (await db.CallAsAsync(WorkTools.Inbox, new { project }, Owner)).GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task An_agent_cannot_resolve_a_question_put_to_the_humans()
    {
        var project = await db.NewProjectAsync("AJ");
        var asker = await StartAsync(project, new WorkActor("claude-s4", WorkActor.AgentKind));
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, kind = "question", to_human = true, body = "Which name?" }, new WorkActor("claude-s4", WorkActor.AgentKind)))
            .GetProperty("message").GetProperty("id").GetString()!;
        var answerer = await StartAsync(project, Agent);

        await RefusedAsync(WorkTools.Post, new { session_id = answerer, in_reply_to = question, resolves = "answered", body = "Use codex." });
        // A reply that adds information without resolving is open to agents.
        (await db.CallAsAsync(WorkTools.Post, new { session_id = answerer, in_reply_to = question, body = "Either works for me." }, Agent)).ValueKind.ShouldBe(JsonValueKind.Object);

        var answered = await db.CallAsAsync(WorkTools.Post, new { project, in_reply_to = question, resolves = "answered", body = "Use codex." }, Owner);
        answered.GetProperty("resolved").GetProperty("state").GetString().ShouldBe("answered");
    }

    [Fact]
    public async Task A_relayed_correction_waits_first_in_the_review_queue_until_the_owner_approves_it()
    {
        var project = await db.NewProjectAsync("AK");
        var item = await db.NewItemAsync(project, "Item");
        var key = item.GetProperty("key").GetString()!;
        var session = await StartAsync(project, Agent);

        // A plain agent proposal first, so the relayed one must outrank it.
        var plain = (await db.CallAsAsync(WorkTools.Record, new { session_id = session, entry = new { kind = "lesson", summary = "Plain proposal", what = "x", applies_when = new { activity = new[] { "testing" } }, source = new { kind = "agent" } } }, Agent))
            .GetProperty("entry");
        await db.CallAsAsync(WorkTools.Promote, new { session_id = session, id = plain.GetProperty("id").GetString(), expected_version = plain.GetProperty("version").GetInt64(), action = "propose" }, Agent);

        var updated = await db.CallAsAsync(WorkTools.UpdateWork, new { session_id = session, id = key, expected_version = item.GetProperty("version").GetInt64(), corrections = new[] { Correction("relayed_prompt") } }, Agent);
        var relayed = updated.GetProperty("recorded").EnumerateArray().Single().GetProperty("id").GetString()!;
        (await RuleStateAsync(relayed)).ShouldBe("proposed");

        var queue = await db.CallAsync(WorkTools.ReviewQueue, new { project });
        var first = queue.GetProperty("items").EnumerateArray().First();
        first.GetProperty("entry").GetProperty("id").GetString().ShouldBe(relayed);
        first.GetProperty("priority").GetString().ShouldBe("highest");

        // Recurrence never promotes it: the same correction relayed again from other events.
        for (var i = 0; i < 4; i++)
            await db.CallAsAsync(WorkTools.EndSession, new { session_id = await StartAsync(project, Agent), corrections = new[] { Correction("relayed_prompt") } }, Agent);
        (await RuleStateAsync(relayed)).ShouldBe("proposed");

        await db.CallAsAsync(WorkTools.Promote, new { id = relayed, expected_version = await VersionAsync(relayed), action = "approve" }, Owner);
        (await RuleStateAsync(relayed)).ShouldBe("rule");
    }

    private async Task RefusedAsync(string tool, object arguments)
    {
        var refused = await Should.ThrowAsync<WorkException>(() => db.CallAsAsync(tool, arguments, Agent));
        refused.Code.ShouldBe(WorkErrorCodes.Forbidden, $"{tool}: {refused.Message}");
        refused.Message.ShouldContain("Nothing was changed");
    }

    private async Task<string> StartAsync(string project, WorkActor actor) =>
        (await db.CallAsAsync(WorkTools.StartSession, new { project, harness = "codex", model = "gpt-5.6" }, actor))
            .GetProperty("session").GetProperty("id").GetString()!;

    private async Task<string> RuleStateAsync(string id)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.QuerySingleAsync<string>("SELECT rule_state FROM knowledge_entries WHERE id = @id", new { id });
    }

    private async Task<long> VersionAsync(string id)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.QuerySingleAsync<long>("SELECT version FROM knowledge_entries WHERE id = @id", new { id });
    }
}
