using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Lessons, rules and challenges (sc-27873) over a real MariaDB, through the work tools as MCP and
/// REST run them: dedup (same_as, exact normalized match, never by similarity alone), recurrence by
/// distinct source event, contradictions kept and served together, prompt lessons as rules at once,
/// rule selection in briefings by applies-when, the served-rule log, challenge, promote and the
/// review queue.
/// </summary>
public sealed class LessonsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    // Repositories are dependencies (a repository is owned by one project only, and tests share the database).
    private async Task<string> ProjectAsync(string prefix, params string[] repositories)
    {
        var key = (prefix + Guid.NewGuid().ToString("N")[..6]).ToUpperInvariant()[..Math.Min(10, prefix.Length + 6)];
        await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new { key, name = $"Project {key}", repositories = repositories.Select(repository => new { repository, role = "dependency" }).ToArray() }
        });
        return key;
    }

    private async Task<string> SessionAsync(string project, string user = "alice", string? repository = null, string[]? activity = null, string harness = "claude-code") =>
        (await db.CallAsync(WorkTools.StartSession, new { project, harness, repository, activity }, user)).GetProperty("session").GetProperty("id").GetString()!;

    private async Task<JsonElement> RecordLessonAsync(string project, string summary, string source, string[]? activity, string? sourceRef = null,
        string? sameAs = null, string? contradicts = null, string? sessionId = null, string[]? repositories = null, string user = "alice")
    {
        var entry = new Dictionary<string, object?>
        {
            ["kind"] = "lesson",
            ["summary"] = summary,
            ["what"] = "Was corrected",
            ["source"] = new { kind = source, @ref = sourceRef },
            ["applies_when"] = activity is null && repositories is null ? null : new { activity, repositories }
        };
        if (sameAs is not null)
            entry["same_as"] = sameAs;
        if (contradicts is not null)
            entry["contradicts"] = contradicts;
        return await db.CallAsync(WorkTools.Record, new { project = sessionId is null ? project : null, session_id = sessionId, entry }, user);
    }

    private async Task<JsonElement> LessonAsync(string id, bool full = false) =>
        (await db.CallAsync(WorkTools.Recall, new { id, detail = full ? "full" : null })).GetProperty("items").EnumerateArray().Single();

    private static string Id(JsonElement recorded) => recorded.GetProperty("entry").GetProperty("id").GetString()!;

    private static List<JsonElement> Rules(JsonElement briefing) =>
        briefing.TryGetProperty("rules", out var rules) ? rules.EnumerateArray().ToList() : [];

    private async Task<JsonElement> BriefingAsync(string project, string[]? activity, string? repository = null, string user = "bob", string[]? skills = null, string harness = "codex") =>
        (await db.CallAsync(WorkTools.StartSession, new { project, harness, repository, activity, skills }, user)).GetProperty("briefing");

    [Fact]
    public async Task Always_and_never_with_the_same_scope_stay_two_rules_both_served_with_the_contradiction_shown()
    {
        var key = await ProjectAsync("LC", "codegraph");
        var always = await RecordLessonAsync(key, "Always rebase onto main before merging", "prompt", ["merging"], "prompt-1");
        var alwaysId = Id(always);
        always.GetProperty("created").GetBoolean().ShouldBeTrue();

        // Similarity lists the candidate and flags the polarity; it never merges on its own.
        var never = await RecordLessonAsync(key, "Never rebase onto main before merging", "prompt", ["merging"], "prompt-2");
        never.GetProperty("created").GetBoolean().ShouldBeTrue();
        never.TryGetProperty("merged", out _).ShouldBeFalse();
        var neverId = Id(never);
        neverId.ShouldNotBe(alwaysId);
        var candidate = never.GetProperty("similar").EnumerateArray().Single().GetProperty("candidates").EnumerateArray().Single();
        candidate.GetProperty("id").GetString().ShouldBe(alwaysId);
        candidate.GetProperty("possible_contradiction").GetBoolean().ShouldBeTrue();

        // The recording agent decides: contradicts. Both are kept, both are rules.
        var current = await LessonAsync(neverId);
        var related = await db.CallAsync(WorkTools.Record, new { relate = new { id = neverId, expected_version = current.GetProperty("version").GetInt64(), contradicts = alwaysId } });
        related.GetProperty("entry").GetProperty("status").GetString().ShouldBe("active");
        (await LessonAsync(alwaysId)).GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
        (await LessonAsync(neverId)).GetProperty("lesson").GetProperty("contradicts").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(alwaysId);

        // Both are served to a merging session, each showing the conflict.
        var rules = Rules(await BriefingAsync(key, ["merging"]));
        rules.Select(rule => rule.GetProperty("id").GetString()).ShouldBe([alwaysId, neverId], ignoreOrder: true);
        rules.Single(rule => rule.GetProperty("id").GetString() == alwaysId).GetProperty("contradicts").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(neverId);
        rules.Single(rule => rule.GetProperty("id").GetString() == neverId).GetProperty("contradicts").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(alwaysId);

        // And the pair is one review-queue item.
        var queue = (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray().ToList();
        var contradiction = queue.Single(item => item.GetProperty("kind").GetString() == "contradiction");
        new[] { contradiction.GetProperty("entry").GetProperty("id").GetString(), contradiction.GetProperty("other").GetProperty("id").GetString() }
            .ShouldBe([alwaysId, neverId], ignoreOrder: true);

        // contradicts passed inline keeps a third lesson apart too, even with the same summary.
        var inline = await RecordLessonAsync(key, "Always rebase onto main before merging", "agent", ["merging"], contradicts: neverId);
        inline.GetProperty("created").GetBoolean().ShouldBeTrue();
        Id(inline).ShouldNotBe(alwaysId);
    }

    [Fact]
    public async Task A_correction_reported_twice_from_one_event_counts_once_and_distinct_events_make_it_a_rule()
    {
        var key = await ProjectAsync("LR", "codegraph");
        var item = await db.NewItemAsync(key, "Add the lessons store");
        var session = await SessionAsync(key, repository: "codegraph");
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = session })).GetProperty("item");
        var correction = new
        {
            what = "Restated the epic requirements in a story",
            correct_behavior = "When slicing a plan, do not restate epic requirements in stories",
            applies_when = new { activity = new[] { "slicing" } },
            source = new { kind = "review", @ref = "https://github.com/o/r/pull/1#discussion_r1" }
        };

        var updated = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(), expected_version = claimed.GetProperty("version").GetInt64(), session_id = session, corrections = new[] { correction }
        });
        var lesson = updated.GetProperty("recorded").EnumerateArray().Single();
        var lessonId = lesson.GetProperty("id").GetString()!;
        lesson.TryGetProperty("rule", out _).ShouldBeFalse();

        // The same correction again in end_session (same review comment): merged, counted once.
        var ended = await db.CallAsync(WorkTools.EndSession, new { session_id = session, corrections = new[] { correction } });
        var merged = ended.GetProperty("recorded").EnumerateArray().Single();
        (merged.GetProperty("id").GetString(), merged.GetProperty("merged").GetBoolean()).ShouldBe((lessonId, true));
        var view = await LessonAsync(lessonId, full: true);
        view.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(1);
        view.GetProperty("lesson").GetProperty("events").EnumerateArray().Select(entry => entry.GetProperty("counted").GetBoolean()).ShouldBe([true, false]);
        view.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("candidate");

        // A retried end answers what the end stored.
        var retried = await db.CallAsync(WorkTools.EndSession, new { session_id = session, corrections = new[] { correction } });
        retried.GetProperty("recorded").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(lessonId);

        // A second, distinct review comment: recurrence 2, the default threshold: a rule.
        var other = await SessionAsync(key, "carol", "codegraph");
        var again = await RecordLessonAsync(key, "When slicing a plan, do not restate epic requirements in stories.", "review", ["slicing"],
            "https://github.com/o/r/pull/2#discussion_r9", sessionId: other, user: "carol");
        (again.GetProperty("merged").GetBoolean(), Id(again)).ShouldBe((true, lessonId));
        var rule = (await LessonAsync(lessonId)).GetProperty("lesson");
        rule.GetProperty("state").GetString().ShouldBe("rule");
        rule.GetProperty("rule_reason").GetString().ShouldBe("recurrence");
        rule.GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);

        // The same second comment reported again by someone else: still 2.
        await RecordLessonAsync(key, "When slicing a plan, do not restate epic requirements in stories", "review", ["slicing"],
            "https://github.com/o/r/pull/2#discussion_r9", user: "dave");
        (await LessonAsync(lessonId)).GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        item.GetProperty("key").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task A_prompt_correction_in_session_A_is_served_as_a_rule_to_session_B_when_its_activity_matches()
    {
        var key = await ProjectAsync("LP", "codegraph", "inference");
        await db.NewItemAsync(key, "Plan the epic");
        var sessionA = await SessionAsync(key, repository: "codegraph", activity: ["planning"]);
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = sessionA })).GetProperty("item");
        var updated = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(), expected_version = claimed.GetProperty("version").GetInt64(), session_id = sessionA,
            corrections = new[]
            {
                new
                {
                    what = "Sliced by enumerated cell",
                    correct_behavior = "Slice by mechanism, not by enumerated cell",
                    applies_when = new { activity = new[] { "slicing" } },
                    source = new { kind = "prompt" }
                }
            }
        });
        var recorded = updated.GetProperty("recorded").EnumerateArray().Single();
        recorded.GetProperty("rule").GetBoolean().ShouldBeTrue();
        var ruleId = recorded.GetProperty("id").GetString()!;
        (await LessonAsync(ruleId)).GetProperty("lesson").GetProperty("rule_reason").GetString().ShouldBe("human_prompt");

        // Session B, another identity and repository, slicing: served the rule. Not to a merging session.
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", repository = "inference", activity = new[] { "slicing", "testing" } }, "bob");
        var sessionB = started.GetProperty("session").GetProperty("id").GetString()!;
        started.GetProperty("session").GetProperty("activity").EnumerateArray().Select(value => value.GetString()).ShouldBe(["slicing", "testing"]);
        Rules(started.GetProperty("briefing")).Single().GetProperty("id").GetString().ShouldBe(ruleId);
        Rules(started.GetProperty("briefing")).Single().GetProperty("priority").GetString().ShouldBe("highest");
        // A rule is served in its own section, not again among knowledge.
        if (started.GetProperty("briefing").TryGetProperty("knowledge", out var knowledge))
            knowledge.EnumerateArray().ShouldNotContain(row => row.GetProperty("id").GetString() == ruleId);
        var merging = await BriefingAsync(key, ["merging"]);
        Rules(merging).ShouldBeEmpty();
        merging.TryGetProperty("rule_activities", out _).ShouldBeFalse();

        // Without an activity no rule is served; rule_activities says which activities have rules.
        var none = await BriefingAsync(key, null);
        Rules(none).ShouldBeEmpty();
        none.GetProperty("rule_activities").EnumerateArray().Select(value => value.GetString()).ShouldBe(["slicing"]);

        // get_briefing with an activity switches the session's activity.
        var sessionC = await SessionAsync(key, "carol", "codegraph", ["merging"]);
        Rules(await db.CallAsync(WorkTools.GetBriefing, new { session_id = sessionC }, "carol")).ShouldBeEmpty();
        Rules(await db.CallAsync(WorkTools.GetBriefing, new { session_id = sessionC, activity = new[] { "slicing" } }, "carol")).Single().GetProperty("id").GetString().ShouldBe(ruleId);
        Rules(await db.CallAsync(WorkTools.GetBriefing, new { session_id = sessionC }, "carol")).ShouldHaveSingleItem();

        // The served-rule log: B was served the rule, so B's recurrence counts as with the rule served.
        await RecordLessonAsync(key, "Slice by mechanism, not by enumerated cell", "agent", ["slicing"], sessionId: sessionB, sameAs: ruleId, user: "bob");
        var stats = (await LessonAsync(ruleId)).GetProperty("lesson");
        stats.GetProperty("recurrence").GetProperty("while_served").GetInt32().ShouldBe(1);
        stats.GetProperty("served_sessions").GetInt32().ShouldBe(2);
        var recurrence = (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "recurrence" })).GetProperty("items").EnumerateArray().Single();
        recurrence.GetProperty("recurrence").GetProperty("while_served").GetInt32().ShouldBe(1);
        recurrence.GetProperty("recurrence").GetProperty("while_not_served").GetInt32().ShouldBe(0);
        (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "recently_learned" })).GetProperty("items").EnumerateArray().Single()
            .GetProperty("rule_reason").GetString().ShouldBe("human_prompt");

        // A session never served it that recurs it: while_not_served.
        var sessionD = await SessionAsync(key, "dave", "codegraph", ["merging"]);
        await RecordLessonAsync(key, "Slice by mechanism, not by enumerated cell", "agent", ["slicing"], sessionId: sessionD, user: "dave");
        (await LessonAsync(ruleId)).GetProperty("lesson").GetProperty("recurrence").GetProperty("while_not_served").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_rules_location_must_match_and_the_briefing_stays_within_budget()
    {
        var key = await ProjectAsync("LL", "codegraph", "inference");
        var scoped = Id(await RecordLessonAsync(key, "Run cargo test with --release on inference", "prompt", ["testing"], repositories: ["inference"]));
        Rules(await BriefingAsync(key, ["testing"], "codegraph")).ShouldBeEmpty();
        Rules(await BriefingAsync(key, ["testing"], "inference")).Single().GetProperty("id").GetString().ShouldBe(scoped);
        Rules(await BriefingAsync(key, ["testing"])).ShouldBeEmpty();

        // Many rules: at most 10 served, and a tight budget cuts rules, naming the section.
        for (var index = 0; index < 14; index++)
            await RecordLessonAsync(key, $"Testing rule number {index} about fixture {index} with a long explanation {new string('x', 150)}", "prompt", ["testing"]);
        var briefing = await BriefingAsync(key, ["testing"]);
        Rules(briefing).Count.ShouldBe(LessonLimits.MaxBriefingRules);
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", activity = new[] { "testing" }, budget_tokens = 400 }, "erin");
        var session = started.GetProperty("session").GetProperty("id").GetString();
        var tight = started.GetProperty("briefing");
        tight.GetProperty("estimated_tokens").GetInt32().ShouldBeLessThanOrEqualTo(400);
        tight.GetProperty("truncated").EnumerateArray().Select(value => value.GetString()).ShouldContain("rules");

        // Only the rules the briefing carried are logged as served to it.
        await using var connection = new MySqlConnection(db.ConnectionString);
        var logged = (await connection.QueryAsync<Guid>("SELECT entry_id FROM lesson_served WHERE session_id = @session", new { session })).ToList();
        var carried = tight.TryGetProperty("rules", out var served) ? served.EnumerateArray().Select(rule => rule.GetProperty("id").GetString()).ToList() : [];
        logged.Select(id => id.ToString()).ShouldBe(carried, ignoreOrder: true);
        carried.Count.ShouldBeLessThan(LessonLimits.MaxBriefingRules);
    }

    [Fact]
    public async Task A_challenge_keeps_the_rule_active_shown_with_the_challenge_until_it_is_resolved()
    {
        var key = await ProjectAsync("LH", "codegraph");
        var ruleId = Id(await RecordLessonAsync(key, "Never run two MLX jobs at once", "prompt", ["testing"]));
        var candidateId = Id(await RecordLessonAsync(key, "Prefer small commits", "agent", ["implementing"]));

        var missing = await db.RefusedAsync(WorkTools.Challenge, new { open = new { id = ruleId, reason = "It is wrong", evidence = Array.Empty<object>() } });
        missing.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var notRule = await db.RefusedAsync(WorkTools.Challenge, new { open = new { id = candidateId, reason = "x", evidence = new[] { new { kind = "link", summary = "y" } } } });
        notRule.Message.ShouldContain("not a rule");

        var opened = await db.CallAsync(WorkTools.Challenge, new
        {
            open = new
            {
                id = ruleId,
                reason = "MLX 0.30 serializes jobs itself",
                evidence = new[] { new { kind = "test_result", summary = "Two concurrent jobs passed 50 runs", @ref = "run 123" } }
            },
            idempotency_key = "challenge-1"
        }, "bob");
        opened.GetProperty("created").GetBoolean().ShouldBeTrue();
        var challengeId = opened.GetProperty("challenge").GetProperty("id").GetString();
        opened.GetProperty("rule").GetProperty("status").GetString().ShouldBe("active");
        (await db.CallAsync(WorkTools.Challenge, new
        {
            open = new { id = ruleId, reason = "MLX 0.30 serializes jobs itself", evidence = new[] { new { kind = "test_result", summary = "Two concurrent jobs passed 50 runs" } } },
            idempotency_key = "challenge-1"
        }, "bob")).GetProperty("created").GetBoolean().ShouldBeFalse();

        // Still served, with the challenge; and in the review queue.
        var rule = Rules(await BriefingAsync(key, ["testing"])).Single();
        rule.GetProperty("id").GetString().ShouldBe(ruleId);
        var shown = rule.GetProperty("challenges").EnumerateArray().Single();
        shown.GetProperty("id").GetString().ShouldBe(challengeId);
        shown.GetProperty("evidence").EnumerateArray().Single().GetString()!.ShouldContain("run 123");
        var queued = (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "challenge");
        queued.GetProperty("entry").GetProperty("id").GetString().ShouldBe(ruleId);

        // Resolved amend: a new revision of the rule; the challenge is closed and leaves the queue.
        var version = (await LessonAsync(ruleId)).GetProperty("version").GetInt64();
        var resolved = await db.CallAsync(WorkTools.Challenge, new
        {
            resolve = new { challenge_id = challengeId, expected_version = version, outcome = "amend", reason = "True since MLX 0.30", amend = new { summary = "Never run two MLX jobs at once before MLX 0.30" } }
        });
        resolved.GetProperty("challenge").GetProperty("status").GetString().ShouldBe("amended");
        resolved.GetProperty("rule").GetProperty("summary").GetString().ShouldBe("Never run two MLX jobs at once before MLX 0.30");
        resolved.GetProperty("rule").GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
        Rules(await BriefingAsync(key, ["testing"])).Single().TryGetProperty("challenges", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray()
            .ShouldNotContain(item => item.GetProperty("kind").GetString() == "challenge");

        var again = await db.RefusedAsync(WorkTools.Challenge, new
        {
            resolve = new { challenge_id = challengeId, expected_version = version + 1, outcome = "uphold", reason = "again" }
        });
        again.Code.ShouldBe(WorkErrorCodes.Conflict);
        var revisions = (await LessonAsync(ruleId, full: true)).GetProperty("revisions").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()).ToList();
        revisions.ShouldContain("amended");
    }

    [Fact]
    public async Task Promote_proposes_approves_rejects_and_marks_addressed_by_and_a_rejected_lesson_is_not_promoted_again()
    {
        var key = await ProjectAsync("LM", "codegraph");
        var lessonId = Id(await RecordLessonAsync(key, "Read workflow states before moving stories", "agent", ["tracker_updates"], "event-1"));

        async Task<JsonElement> PromoteAsync(string action, object? extra = null)
        {
            var arguments = JsonSerializer.SerializeToNode(extra ?? new { }, WorkJson.Options)!.AsObject();
            arguments["id"] = lessonId;
            arguments["action"] = action;
            arguments["expected_version"] = (await LessonAsync(lessonId)).GetProperty("version").GetInt64();
            return await db.CallAsync(WorkTools.Promote, arguments);
        }

        var proposed = await PromoteAsync("propose");
        (proposed.GetProperty("changed").GetBoolean(), proposed.GetProperty("entry").GetProperty("lesson").GetProperty("state").GetString()).ShouldBe((true, "proposed"));
        (await PromoteAsync("propose")).GetProperty("changed").GetBoolean().ShouldBeFalse();
        (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("kind").GetString() == "proposed_rule");

        (await db.RefusedAsync(WorkTools.Promote, new { id = lessonId, expected_version = 1, action = "reject" })).Message.ShouldContain("reason");
        var rejected = await PromoteAsync("reject", new { reason = "Too specific to one tracker" });
        rejected.GetProperty("entry").GetProperty("lesson").GetProperty("rejected_reason").GetString().ShouldBe("Too specific to one tracker");
        var reproposed = await db.RefusedAsync(WorkTools.Promote, new { id = lessonId, expected_version = (await LessonAsync(lessonId)).GetProperty("version").GetInt64(), action = "propose" });
        reproposed.Code.ShouldBe(WorkErrorCodes.Conflict);
        reproposed.Message.ShouldContain("Too specific");

        // Recurrence does not promote a rejected lesson.
        await RecordLessonAsync(key, "Read workflow states before moving stories", "agent", ["tracker_updates"], "event-2");
        await RecordLessonAsync(key, "Read workflow states before moving stories", "agent", ["tracker_updates"], "event-3");
        (await LessonAsync(lessonId)).GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rejected");
        Rules(await BriefingAsync(key, ["tracker_updates"])).ShouldBeEmpty();

        // approve overrides; addressed_by takes it out of briefings and out of unaddressed.
        (await PromoteAsync("approve")).GetProperty("entry").GetProperty("lesson").GetProperty("rule_reason").GetString().ShouldBe("approved");
        Rules(await BriefingAsync(key, ["tracker_updates"])).Single().GetProperty("recurrence").GetInt32().ShouldBe(3);
        var unaddressed = (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "unaddressed" })).GetProperty("groups").EnumerateArray().Single();
        unaddressed.GetProperty("activity").GetString().ShouldBe("tracker_updates");
        var addressed = await PromoteAsync("addressed_by", new { addressed_by = new { kind = "skill", name = "shortcut-story", @ref = "abc123" } });
        addressed.GetProperty("entry").GetProperty("lesson").GetProperty("addressed_by").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("shortcut-story");
        // A skill carries the rule only for sessions that load it.
        Rules(await BriefingAsync(key, ["tracker_updates"], skills: ["Shortcut-Story"])).ShouldBeEmpty();
        Rules(await BriefingAsync(key, ["tracker_updates"])).ShouldHaveSingleItem();
        (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "unaddressed" })).GetProperty("groups").EnumerateArray().ShouldBeEmpty();

        // A lesson without an applies-when activity is not made a rule.
        var bare = Id(await RecordLessonAsync(key, "Check the CI logs", "prompt", null));
        (await LessonAsync(bare)).GetProperty("lesson").GetProperty("state").GetString().ShouldBe("candidate");
        var refused = await db.RefusedAsync(WorkTools.Promote, new { id = bare, expected_version = (await LessonAsync(bare)).GetProperty("version").GetInt64(), action = "approve" });
        refused.Message.ShouldContain("applies-when activity");
    }

    [Fact]
    public async Task An_exact_normalized_match_merges_only_with_overlapping_applies_when_and_relate_merges_a_duplicate()
    {
        var key = await ProjectAsync("LX", "codegraph");
        var first = Id(await RecordLessonAsync(key, "Run the tests before pushing.", "agent", ["testing"], "e1"));
        var same = await RecordLessonAsync(key, "run the tests   before pushing", "agent", ["testing", "merging"], "e2");
        (same.GetProperty("merged").GetBoolean(), Id(same)).ShouldBe((true, first));
        var disjoint = await RecordLessonAsync(key, "Run the tests before pushing", "agent", ["releasing"], "e3");
        Id(disjoint).ShouldNotBe(first);

        // A paraphrase is only a candidate; relate same_as merges it, its events become recurrences.
        var paraphrase = await RecordLessonAsync(key, "Run the test suite before pushing", "agent", ["testing"], "e4");
        var paraphraseId = Id(paraphrase);
        paraphrase.GetProperty("similar").EnumerateArray().Single().GetProperty("candidates").EnumerateArray()
            .ShouldContain(candidate => candidate.GetProperty("id").GetString() == first);
        var version = (await LessonAsync(paraphraseId)).GetProperty("version").GetInt64();
        var related = await db.CallAsync(WorkTools.Record, new { relate = new { id = paraphraseId, expected_version = version, same_as = first } });
        (related.GetProperty("merged").GetBoolean(), related.GetProperty("entry").GetProperty("id").GetString()).ShouldBe((true, first));
        var merged = (await LessonAsync(first)).GetProperty("lesson");
        merged.GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(3);
        merged.GetProperty("state").GetString().ShouldBe("rule");
        (await LessonAsync(paraphraseId)).GetProperty("status").GetString().ShouldBe("superseded");

        // same_as must name an active lesson of the project.
        var unknown = await db.RefusedAsync(WorkTools.Record, new
        {
            project = key,
            entry = new { kind = "lesson", summary = "x", source = new { kind = "agent" }, same_as = Guid.NewGuid().ToString() }
        });
        unknown.Code.ShouldBe(WorkErrorCodes.NotFound);
        var both = await db.RefusedAsync(WorkTools.Record, new
        {
            project = key,
            entry = new { kind = "lesson", summary = "x", source = new { kind = "agent" }, same_as = first, contradicts = first }
        });
        both.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Record, new { project = key, entry = new { kind = "fact", summary = "x", same_as = first } })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Autonomous_decisions_are_in_the_review_queue_until_acknowledged_and_nothing_there_blocks_work()
    {
        var key = await ProjectAsync("LD", "codegraph");
        var item = await db.NewItemAsync(key, "Pick the similarity");
        var decided = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = item.GetProperty("key").GetString(),
            expected_version = item.GetProperty("version").GetInt64(),
            status = "in_progress",
            decisions = new[] { new { decision = "Lexical similarity, not embeddings", made_autonomously = true } }
        });
        decided.GetProperty("item").GetProperty("status").GetString().ShouldBe("in_progress");
        var queue = (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray().ToList();
        var decision = queue.Single(entry => entry.GetProperty("kind").GetString() == "autonomous_decision").GetProperty("entry");

        // Work proceeds regardless: the item completes with the decision unreviewed.
        (await db.UpdateAsync(item.GetProperty("key").GetString()!, new { status = "complete" })).GetProperty("status").GetString().ShouldBe("complete");

        await db.CallAsync(WorkTools.Record, new { acknowledge = new { id = decision.GetProperty("id").GetString(), expected_version = decision.GetProperty("version").GetInt64() } });
        (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_identical_corrections_make_one_lesson_with_one_recurrence_per_distinct_event()
    {
        var key = await ProjectAsync("LK");
        // Eight writers, four distinct events each reported twice, all at once.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            RecordLessonAsync(key, "Pin the inference revision in the same PR", "bot_review", ["merging"], $"comment-{index % 4}", user: $"writer{index}")));
        var ids = results.Select(Id).Distinct().ToList();
        ids.ShouldHaveSingleItem();
        var lesson = (await LessonAsync(ids[0], full: true)).GetProperty("lesson");
        lesson.GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(4);
        lesson.GetProperty("events").GetArrayLength().ShouldBe(8);
        lesson.GetProperty("state").GetString().ShouldBe("rule");
    }
}
