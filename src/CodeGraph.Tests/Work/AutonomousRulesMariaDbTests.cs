using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// No rule waits for the owner's approval and no autonomous decision for acknowledgment (CG-174):
/// a correction from the owner's prompt or review, direct or relayed by an agent, is a rule at once
/// and keeps the owner's words; an inferred lesson (agent, subagent, bot_review) becomes a rule by
/// recurrence from distinct sources, never from one session repeating itself; the review queue
/// holds only challenges and contradictions; migration 118 settles what was left waiting.
/// </summary>
public sealed class AutonomousRulesMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static object Correction(string source, string? reference = null, string? quote = null, string activity = "reviewing", string text = "Check the source before answering") => new
    {
        what = "Answered without checking",
        correct_behavior = text,
        applies_when = new { activity = new[] { activity } },
        source = new { kind = source, @ref = reference ?? Guid.NewGuid().ToString(), quote }
    };

    [Fact]
    public async Task A_relayed_or_direct_correction_from_the_owner_is_a_rule_at_once_and_keeps_the_owners_words()
    {
        var project = await db.NewProjectAsync("AA");
        var agent = await db.AgentAsync("codex-aa");
        var session = await StartAsync(project, agent);
        var item = (await db.NewItemAsync(project, "Item")).GetProperty("key").GetString()!;

        const string quote = "The less I have to interact and approve the better";
        var relayed = await CorrectAsync(item, Correction("relayed_prompt", "prompt:owner-1", quote, text: "Do not ask the owner to approve rules"), agent, session);
        (await RuleAsync(relayed)).ShouldBe(("rule", "human_prompt"));
        var full = await LessonAsync(relayed);
        full.GetProperty("details").GetProperty("source").GetProperty("ref").GetString().ShouldBe("prompt:owner-1");
        full.GetProperty("details").GetProperty("source").GetProperty("quote").GetString().ShouldBe(quote);
        full.GetProperty("lesson").GetProperty("events").EnumerateArray().Single().GetProperty("source_quote").GetString().ShouldBe(quote);

        var review = await CorrectAsync(item, Correction("relayed_review", "https://github.com/o/r/pull/7#discussion_r1", activity: "merging", text: "Merge on green CI"), agent, session);
        (await RuleAsync(review)).ShouldBe(("rule", "human_review"));

        // The owner's own review, recorded with the owner's token, is a rule at once too.
        var direct = (await db.CallAsync(WorkTools.Record, new
        {
            project,
            entry = new { kind = "lesson", summary = "Name tests after behavior", what = "w", applies_when = new { activity = new[] { "testing" } }, source = new { kind = "review", @ref = "r-9" } }
        }, "michael")).GetProperty("entry").GetProperty("id").GetString()!;
        (await RuleAsync(direct)).ShouldBe(("rule", "human_review"));

        // Served to a matching session at once, and nothing waits in the review queue.
        var rules = (await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex", activity = new[] { "reviewing", "merging", "testing" } }, "bob"))
            .GetProperty("briefing").GetProperty("rules").EnumerateArray().Select(rule => rule.GetProperty("id").GetString()).ToList();
        rules.ShouldBe([relayed, review, direct], ignoreOrder: true);
        (await QueueAsync(project)).GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task An_inferred_lesson_becomes_a_rule_only_from_distinct_sources_never_from_one_session_repeating_itself()
    {
        var project = await db.NewProjectAsync("AB");
        var codex = await db.AgentAsync("codex-ab");
        var claude = await db.AgentAsync("claude-ab");
        var first = await StartAsync(project, codex);
        var items = new List<string>();
        for (var i = 0; i < 3; i++)
            items.Add((await db.NewItemAsync(project, $"Item {i}")).GetProperty("key").GetString()!);

        // One session on two items with two refs: two recurrence events, one source, still a candidate.
        var lesson = await CorrectAsync(items[0], Correction("agent", "a-1"), codex, first);
        (await CorrectAsync(items[1], Correction("agent", "a-2"), codex, first)).ShouldBe(lesson);
        (await LessonAsync(lesson)).GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        (await RuleAsync(lesson)).ShouldBe(("candidate", null));
        // Subagent and automated-review corrections from that same session are still that one source.
        (await CorrectAsync(items[2], Correction("subagent", "a-3"), codex, first)).ShouldBe(lesson);
        (await CorrectAsync(items[2], Correction("bot_review", "https://github.com/o/r/pull/1#discussion_r2"), codex, first)).ShouldBe(lesson);
        (await RuleAsync(lesson)).ShouldBe(("candidate", null));

        // Another session of the same agent is a second source: a rule, with no approval.
        var second = await StartAsync(project, codex);
        (await CorrectAsync(items[0], Correction("agent", "a-4"), codex, second)).ShouldBe(lesson);
        (await RuleAsync(lesson)).ShouldBe(("rule", "recurrence"));

        // Without sessions, the identity is the source: one agent repeating itself does not promote; another agent does.
        var bare = await RecordAsync(project, "Read the workflow states first", "agent", "b-1", codex);
        (await RecordAsync(project, "Read the workflow states first", "agent", "b-2", codex)).ShouldBe(bare);
        (await RuleAsync(bare)).ShouldBe(("candidate", null));
        (await RecordAsync(project, "Read the workflow states first", "bot_review", "b-3", claude)).ShouldBe(bare);
        (await RuleAsync(bare)).ShouldBe(("rule", "recurrence"));
    }

    [Fact]
    public async Task The_review_queue_holds_only_challenges_and_contradictions_and_autonomous_decisions_stay_readable()
    {
        var project = await db.NewProjectAsync("AC", "codegraph");
        var agent = await db.AgentAsync("codex-ac");
        var item = await db.NewItemAsync(project, "Item");
        var key = item.GetProperty("key").GetString()!;
        var session = await StartAsync(project, agent);
        await db.CallAsAsync(WorkTools.NextWork, new { project, session_id = session }, agent);

        var decided = await db.CallAsAsync(WorkTools.UpdateWork, new
        {
            id = key, session_id = session, expected_version = await ItemVersionAsync(key), status = "in_progress",
            decisions = new[] { new { decision = "Lexical similarity, not embeddings", made_autonomously = true } }
        }, agent);
        var decision = decided.GetProperty("recorded").EnumerateArray().Single().GetProperty("id").GetString()!;
        var candidate = await RecordAsync(project, "Pin the revision in the same PR", "agent", "c-1", agent);
        var proposed = await RecordAsync(project, "Run the linter before pushing", "agent", "c-2", agent);
        await db.CallAsAsync(WorkTools.Promote, new { id = proposed, expected_version = await VersionAsync(proposed), action = "propose" }, agent);

        // Neither a proposed or candidate lesson nor an autonomous decision is a queue item.
        var queue = await QueueAsync(project);
        (queue.GetProperty("items").GetArrayLength(), queue.GetProperty("total").GetInt32()).ShouldBe((0, 0));

        // The decision stays readable: recall, the item brief and the holding session's briefing.
        (await db.CallAsync(WorkTools.Recall, new { project, kind = new[] { "decision" } })).GetProperty("items").EnumerateArray().Single()
            .GetProperty("made_autonomously").GetBoolean().ShouldBeTrue();
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("knowledge").EnumerateArray()
            .ShouldContain(entry => entry.GetProperty("id").GetString() == decision);
        (await db.CallAsAsync(WorkTools.GetBriefing, new { session_id = session }, agent)).GetProperty("knowledge").EnumerateArray()
            .ShouldContain(entry => entry.GetProperty("id").GetString() == decision);

        // The lessons that are not rules yet are listed by view candidates.
        var candidates = (await db.CallAsync(WorkTools.Lessons, new { project, view = "candidates" })).GetProperty("items").EnumerateArray()
            .Select(row => (row.GetProperty("id").GetString(), row.GetProperty("state").GetString())).ToList();
        candidates.ShouldBe([(proposed, "proposed"), (candidate, "candidate")], ignoreOrder: true);

        // What an agent cannot resolve alone still waits: a challenge and a contradiction.
        var rule = (await db.CallAsync(WorkTools.Record, new
        {
            project, entry = new { kind = "lesson", summary = "Always squash merge", what = "w", applies_when = new { activity = new[] { "merging" } }, source = new { kind = "prompt" } }
        }, "michael")).GetProperty("entry").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.Challenge, new { open = new { id = rule, reason = "History is lost", evidence = new[] { new { kind = "link", summary = "See PR", @ref = "https://x" } } } }, agent);
        await db.CallAsAsync(WorkTools.Record, new
        {
            project, entry = new { kind = "lesson", summary = "Never squash merge", what = "w", applies_when = new { activity = new[] { "merging" } }, source = new { kind = "agent" }, contradicts = rule }
        }, agent);
        (await QueueAsync(project)).GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()).Order()
            .ShouldBe(["challenge", "contradiction"]);
    }

    [Fact]
    public void The_tool_texts_say_rules_need_no_approval_and_only_a_decision_made_without_the_owner_is_autonomous()
    {
        static string Description(Type type, string property) => type.GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var text in new[] { Description(typeof(DecisionInput), "MadeAutonomously"), Description(typeof(KnowledgeEntryInput), "MadeAutonomously"),
                     Description(typeof(KnowledgeRevisionInput), "MadeAutonomously") })
        {
            text.ShouldContain("approved, including one approved in chat");
            text.ShouldContain("is not autonomous");
            text.ShouldNotContain("review queue");
        }

        var source = Description(typeof(CorrectionSourceInput), "Kind");
        source.ShouldContain("rule at once, no approval");
        source.ShouldNotContain("until the owner approves");
        var updateWork = WorkTools.Find(WorkTools.UpdateWork)!.Description;
        updateWork.ShouldContain("relayed_prompt / relayed_review");
        updateWork.ShouldContain("never makes it a rule");
        updateWork.ShouldContain("one the owner made or approved, in chat included, is not");
        var queue = WorkTools.Find(WorkTools.ReviewQueue)!.Description;
        queue.ShouldNotContain("not yet acknowledged");
        queue.ShouldNotContain("lessons proposed for rule status");
    }

    [Fact]
    public async Task Migration_118_makes_waiting_relayed_and_review_lessons_rules_and_leaves_the_rest()
    {
        var project = await db.NewProjectAsync("AD");
        var agent = await db.AgentAsync("codex-ad");
        var relayedPrompt = await RecordAsync(project, "Relayed prompt lesson", "relayed_prompt", "m-1", agent);
        var relayedReview = await RecordAsync(project, "Relayed review lesson", "relayed_review", "m-2", agent);
        var ownersReview = (await db.CallAsync(WorkTools.Record, new
        {
            project, entry = new { kind = "lesson", summary = "Owner review lesson", what = "w", applies_when = new { activity = new[] { "reviewing" } }, source = new { kind = "review" } }
        }, "michael")).GetProperty("entry").GetProperty("id").GetString()!;
        // An agent's lesson a relayed correction was merged into.
        var merged = await RecordAsync(project, "Merged lesson", "agent", "m-3", agent);
        (await RecordAsync(project, "Merged lesson", "relayed_prompt", "m-4", agent)).ShouldBe(merged);
        var agentProposal = await RecordAsync(project, "Agent proposal", "agent", "m-5", agent);
        var rejected = await RecordAsync(project, "Rejected relayed lesson", "relayed_prompt", "m-6", agent);

        // The state these were left in before CG-174.
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE knowledge_entries SET rule_state = 'proposed', rule_reason = NULL, rule_since = NULL WHERE id IN @ids",
            new { ids = new[] { relayedPrompt, relayedReview, merged, agentProposal } });
        await connection.ExecuteAsync("UPDATE knowledge_entries SET rule_state = 'candidate', rule_reason = NULL, rule_since = NULL WHERE id = @ownersReview", new { ownersReview });
        await connection.ExecuteAsync("UPDATE knowledge_entries SET rule_state = 'rejected', rule_reason = NULL, rule_since = NULL, rejected_reason = 'no' WHERE id = @rejected", new { rejected });

        var migration = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations/118_rules_without_approval.sql"));
        foreach (var statement in MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(migration)))
            await connection.ExecuteAsync(statement);

        (await RuleAsync(relayedPrompt)).ShouldBe(("rule", "human_prompt"));
        (await RuleAsync(relayedReview)).ShouldBe(("rule", "human_review"));
        (await RuleAsync(ownersReview)).ShouldBe(("rule", "human_review"));
        (await RuleAsync(merged)).ShouldBe(("rule", "human_prompt"));
        (await RuleAsync(agentProposal)).ShouldBe(("proposed", null));
        (await RuleAsync(rejected)).ShouldBe(("rejected", null));

        // Restart-safe: a second run changes nothing.
        var since = await connection.QuerySingleAsync<DateTime>("SELECT rule_since FROM knowledge_entries WHERE id = @relayedPrompt", new { relayedPrompt });
        foreach (var statement in MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(migration)))
            await connection.ExecuteAsync(statement);
        (await connection.QuerySingleAsync<DateTime>("SELECT rule_since FROM knowledge_entries WHERE id = @relayedPrompt", new { relayedPrompt })).ShouldBe(since);
    }

    private async Task<string> StartAsync(string project, WorkActor actor) =>
        (await db.CallAsAsync(WorkTools.StartSession, new { project, harness = "codex", model = "gpt-5.6" }, actor)).GetProperty("session").GetProperty("id").GetString()!;

    private async Task<long> ItemVersionAsync(string key) => (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64();

    // A correction on an item through update_work: answers the lesson it was stored as or merged into.
    private async Task<string> CorrectAsync(string item, object correction, WorkActor actor, string session) =>
        (await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, session_id = session, expected_version = await ItemVersionAsync(item), corrections = new[] { correction } }, actor))
        .GetProperty("recorded").EnumerateArray().Single().GetProperty("id").GetString()!;

    private async Task<string> RecordAsync(string project, string summary, string source, string reference, WorkActor actor) =>
        (await db.CallAsAsync(WorkTools.Record, new
        {
            project, entry = new { kind = "lesson", summary, what = "w", applies_when = new { activity = new[] { "testing" } }, source = new { kind = source, @ref = reference } }
        }, actor)).GetProperty("entry").GetProperty("id").GetString()!;

    private async Task<JsonElement> LessonAsync(string id) =>
        (await db.CallAsync(WorkTools.Recall, new { id, detail = "full" })).GetProperty("items").EnumerateArray().Single();

    private async Task<long> VersionAsync(string id) => (await LessonAsync(id)).GetProperty("version").GetInt64();

    private async Task<JsonElement> QueueAsync(string project) => await db.CallAsync(WorkTools.ReviewQueue, new { project });

    private async Task<(string State, string? Reason)> RuleAsync(string id)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.QuerySingleAsync<(string, string?)>("SELECT rule_state, rule_reason FROM knowledge_entries WHERE id = @id", new { id });
    }
}
