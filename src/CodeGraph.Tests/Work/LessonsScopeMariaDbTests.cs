using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Lessons (sc-27873) review batch over a real MariaDB: a merge unites applies-when (a prompt
/// correction makes a task-scoped lesson a rule at once), the derived default activity, addressed_by
/// scope (skill, harness, test), embedding similarity with a deterministic fake embedder, review
/// comments with no lesson, supersede carrying the rule over, and the item brief's rules.
/// </summary>
public sealed class LessonsScopeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private async Task<string> ProjectAsync(string prefix, params string[] repositories)
    {
        var key = (prefix + Guid.NewGuid().ToString("N")[..6]).ToUpperInvariant()[..Math.Min(10, prefix.Length + 6)];
        await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new { key, name = $"Project {key}", repositories = repositories.Select(repository => new { repository, role = "dependency" }).ToArray() }
        });
        return key;
    }

    private async Task<JsonElement> RecordAsync(string project, string summary, string source, string[]? activity, string? sourceRef = null, string user = "alice",
        object? supersedes = null, string? sameAs = null) =>
        await db.CallAsync(WorkTools.Record, new
        {
            project,
            entry = new
            {
                kind = "lesson", summary, what = "Was corrected", source = new { kind = source, @ref = sourceRef },
                applies_when = activity is null ? null : new { activity }, supersedes, same_as = sameAs
            }
        }, user);

    private async Task<JsonElement> LessonAsync(string id) =>
        (await db.CallAsync(WorkTools.Recall, new { id, detail = "full" })).GetProperty("items").EnumerateArray().Single();

    private static string Id(JsonElement recorded) => recorded.GetProperty("entry").GetProperty("id").GetString()!;

    private static List<string?> RuleIds(JsonElement briefing) =>
        briefing.TryGetProperty("rules", out var rules) ? rules.EnumerateArray().Select(rule => rule.GetProperty("id").GetString()).ToList() : [];

    private async Task<JsonElement> BriefingAsync(string project, string[]? activity = null, string harness = "codex", string[]? skills = null, string user = "bob") =>
        (await db.CallAsync(WorkTools.StartSession, new { project, harness, activity, skills }, user)).GetProperty("briefing");

    [Fact]
    public async Task A_prompt_correction_with_an_activity_merged_into_a_task_scoped_lesson_makes_it_a_rule_at_once()
    {
        var key = await ProjectAsync("SB");
        var bare = await RecordAsync(key, "Read the workflow states before moving a story", "prompt", null, "prompt-1");
        var lessonId = Id(bare);
        // Stored task-scoped, and the answer says why.
        bare.GetProperty("hints").EnumerateArray().Single().GetString()!.ShouldContain("applies_when.activity");
        (await LessonAsync(lessonId)).GetProperty("lesson").GetProperty("state").GetString().ShouldBe("candidate");

        var again = await RecordAsync(key, "Read the workflow states before moving a story", "prompt", ["tracker_updates"], "prompt-2");
        (again.GetProperty("merged").GetBoolean(), Id(again)).ShouldBe((true, lessonId));
        again.TryGetProperty("hints", out _).ShouldBeFalse();
        var lesson = await LessonAsync(lessonId);
        lesson.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
        lesson.GetProperty("lesson").GetProperty("rule_reason").GetString().ShouldBe("human_prompt");
        lesson.GetProperty("details").GetProperty("applies_when").GetProperty("activity").EnumerateArray().Single().GetString().ShouldBe("tracker_updates");
        RuleIds(await BriefingAsync(key, ["tracker_updates"])).ShouldBe([lessonId]);

        // The same event reported again with another activity (same_as: an exact match needs overlapping applies-when) widens the trigger without counting.
        await RecordAsync(key, "Read the workflow states before moving a story", "prompt", ["coordinating"], "prompt-2", sameAs: lessonId);
        var widened = await LessonAsync(lessonId);
        widened.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        widened.GetProperty("details").GetProperty("applies_when").GetProperty("activity").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["tracker_updates", "coordinating"], ignoreOrder: true);
        widened.GetProperty("revisions").EnumerateArray().Select(revision => revision.GetProperty("action").GetString()).ShouldContain("widened");
    }

    [Fact]
    public async Task A_session_without_an_activity_is_served_the_rules_its_held_items_imply_and_the_item_brief_serves_rules_too()
    {
        var key = await ProjectAsync("SD");
        var implementing = Id(await RecordAsync(key, "Keep migrations restart-safe", "prompt", ["implementing"]));
        var reviewing = Id(await RecordAsync(key, "Check the lock order in every write", "prompt", ["reviewing"]));

        // Nothing held: no default, the hint remains.
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "claude-code" });
        var session = started.GetProperty("session").GetProperty("id").GetString();
        RuleIds(started.GetProperty("briefing")).ShouldBeEmpty();
        started.GetProperty("briefing").GetProperty("rule_activities").EnumerateArray().Select(value => value.GetString()).ShouldBe(["implementing", "reviewing"]);

        // Holding an in-progress item: implementing.
        await db.NewItemAsync(key, "Write the migration");
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = session })).GetProperty("item");
        var moved = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(), expected_version = claimed.GetProperty("version").GetInt64(), session_id = session, status = "in_progress"
        });
        var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = session });
        RuleIds(briefing).ShouldBe([implementing]);
        briefing.GetProperty("derived_activity").EnumerateArray().Single().GetString().ShouldBe("implementing");

        // The item brief: in_review implies reviewing; read with the session, it is logged as served.
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(), expected_version = moved.GetProperty("item").GetProperty("version").GetInt64(), session_id = session, status = "in_review"
        });
        var brief = await db.CallAsync(WorkTools.GetWork, new { id = claimed.GetProperty("key").GetString(), session_id = session });
        brief.GetProperty("rules").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(reviewing);
        if (brief.TryGetProperty("knowledge", out var knowledge))
            knowledge.EnumerateArray().ShouldNotContain(row => row.GetProperty("kind").GetString() == "lesson");
        (await LessonAsync(reviewing)).GetProperty("lesson").GetProperty("served_sessions").GetInt32().ShouldBe(1);

        // A declared activity wins over the derived one.
        RuleIds(await db.CallAsync(WorkTools.GetBriefing, new { session_id = session, activity = new[] { "implementing" } })).ShouldBe([implementing]);
    }

    [Fact]
    public async Task Addressed_by_hides_a_rule_by_scope_skill_per_session_hook_per_harness_test_everywhere()
    {
        var key = await ProjectAsync("SA");
        var bySkill = Id(await RecordAsync(key, "Slice stories by mechanism", "prompt", ["slicing"]));
        var byHook = Id(await RecordAsync(key, "Run the formatter before committing", "prompt", ["slicing"]));
        var byTest = Id(await RecordAsync(key, "Plans hold at most twelve stories", "prompt", ["slicing"]));

        async Task<JsonElement> AddressAsync(string id, object addressedBy) => await db.CallAsync(WorkTools.Promote, new
        {
            id, expected_version = (await LessonAsync(id)).GetProperty("version").GetInt64(), action = "addressed_by", addressed_by = addressedBy
        });

        (await db.RefusedAsync(WorkTools.Promote, new { id = byHook, expected_version = 1, action = "addressed_by", addressed_by = new { kind = "hook", name = "pre-commit" } }))
            .Message.ShouldContain("needs harness");
        (await db.RefusedAsync(WorkTools.Promote, new { id = bySkill, expected_version = 1, action = "addressed_by", addressed_by = new { kind = "skill", name = "s", harness = "codex" } }))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        await AddressAsync(bySkill, new { kind = "skill", name = "shortcut-plan", @ref = "v1" });
        await AddressAsync(byHook, new { kind = "hook", name = "pre-commit", harness = "claude-code" });
        await AddressAsync(byTest, new { kind = "test", name = "PlanLimitsTests" });

        RuleIds(await BriefingAsync(key, ["slicing"], "codex")).ShouldBe([bySkill, byHook], ignoreOrder: true);
        RuleIds(await BriefingAsync(key, ["slicing"], "claude-code")).ShouldBe([bySkill]);
        RuleIds(await BriefingAsync(key, ["slicing"], "claude-code", ["shortcut-plan"])).ShouldBeEmpty();
        // Skills are kept on the session; get_briefing replaces them.
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", activity = new[] { "slicing" }, skills = new[] { "shortcut-plan" } }, "bob");
        var session = started.GetProperty("session").GetProperty("id").GetString();
        RuleIds(started.GetProperty("briefing")).ShouldBe([byHook]);
        RuleIds(await db.CallAsync(WorkTools.GetBriefing, new { session_id = session }, "bob")).ShouldBe([byHook]);
        RuleIds(await db.CallAsync(WorkTools.GetBriefing, new { session_id = session, skills = new[] { "other" } }, "bob")).ShouldBe([bySkill, byHook], ignoreOrder: true);
        RuleIds(await db.CallAsync(WorkTools.GetBriefing, new { session_id = session, skills = new[] { "shortcut-plan" } }, "bob")).ShouldBe([byHook]);

        // A refused get_briefing changes neither activity nor skills.
        await db.RefusedAsync(WorkTools.GetBriefing, new { session_id = session, item = "NOPE-1", activity = new[] { "merging" }, skills = new[] { "other" } }, "bob");
        RuleIds(await db.CallAsync(WorkTools.GetBriefing, new { session_id = session }, "bob")).ShouldBe([byHook]);

        // The same skill with a new ref updates the link with a revision; the same ref changes nothing.
        var updated = await AddressAsync(bySkill, new { kind = "skill", name = "shortcut-plan", @ref = "v2" });
        updated.GetProperty("changed").GetBoolean().ShouldBeTrue();
        updated.GetProperty("entry").GetProperty("lesson").GetProperty("addressed_by").EnumerateArray().Single().GetProperty("ref").GetString().ShouldBe("v2");
        (await AddressAsync(bySkill, new { kind = "skill", name = "shortcut-plan", @ref = "v2" })).GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    private sealed class FakeEmbedder : IEmbeddingService
    {
        // Each dimension is one concept; a text's vector counts its words per concept. Deterministic, offline.
        private static readonly string[][] Concepts =
        [
            ["ship", "deploy", "release", "publish"],
            ["qa", "test", "tests", "testing", "verified"],
            ["pin", "revision", "inference"],
            ["story", "stories", "slice", "slicing"]
        ];

        public int Calls { get; private set; }
        public bool IsAvailable => true;
        public string ModelName => "fake-concepts-1";
        public int Dimensions => Concepts.Length;

        public float[] GenerateEmbedding(string text) => GenerateEmbeddings([text])[0];

        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts)
        {
            Calls++;
            return texts.Select(text =>
            {
                var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return Concepts.Select(concept => (float)words.Count(concept.Contains)).ToArray();
            }).ToList();
        }
    }

    [Fact]
    public async Task Embeddings_find_paraphrases_the_lexical_score_misses_and_never_merge_them()
    {
        var embedder = new FakeEmbedder();
        var service = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }),
            embeddingService: embedder, lessonOptions: Options.Create(new WorkLessonOptions { EmbeddingCandidateThreshold = 0.9 }));
        async Task<JsonElement> CallAsync(string tool, object arguments)
        {
            var result = await service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor("alice"));
            return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
        }

        var key = await ProjectAsync("SE");
        var first = Id(await CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Ship only after QA", source = new { kind = "agent" } } }));
        await CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Pin the inference revision", source = new { kind = "agent" } } });
        var paraphrase = await CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Deploy once testing is done", source = new { kind = "agent" } } });

        paraphrase.GetProperty("created").GetBoolean().ShouldBeTrue();
        var candidate = paraphrase.GetProperty("similar").EnumerateArray().Single().GetProperty("candidates").EnumerateArray().Single();
        (candidate.GetProperty("id").GetString(), candidate.GetProperty("method").GetString()).ShouldBe((first, "embedding"));
        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.9);
        LessonSimilarity.Score("Deploy once testing is done", "Ship only after QA").ShouldBe(0);

        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM lesson_embeddings le JOIN knowledge_entries e ON e.id = le.entry_id JOIN work_projects p ON p.id = e.project_id WHERE p.project_key = @key AND le.model = 'fake-concepts-1'",
            new { key })).ShouldBe(3);

        // Without a provider the same lookup is lexical, and lists nothing here.
        var lexical = await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Release when verified", source = new { kind = "agent" } } });
        lexical.TryGetProperty("similar", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Review_comments_with_no_lesson_are_listed_until_a_lesson_names_them()
    {
        var key = await ProjectAsync("SR");
        var item = await db.NewItemAsync(key, "Review me");
        await using (var connection = new MySqlConnection(db.ConnectionString))
        {
            var itemId = item.GetProperty("id").GetString();
            foreach (var (externalId, kind, source, body) in new[] { ("101", "review_comment", "human", "Use the lock order"), ("102", "review_comment", "bot", "Nit: rename"), ("103", "review", "human", "") })
                await connection.ExecuteAsync(
                    "INSERT INTO work_github_evidence (id, item_id, kind, evidence_key, repository, pr_number, url, external_id, author_login, source, priority, body, created_at, updated_at) " +
                    "VALUES (UUID(), @itemId, @kind, @externalId, 'o/r', 7, @url, @externalId, 'reviewer', @source, 'high', @body, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
                    new { itemId, kind, externalId, url = $"https://github.com/o/r/pull/7#discussion_r{externalId}", source, body });
        }

        var unlearned = (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "unlearned_reviews" })).GetProperty("reviews").EnumerateArray().ToList();
        unlearned.Select(review => review.GetProperty("external_id").GetString()).ShouldBe(["101", "102"], ignoreOrder: true);
        unlearned.First().GetProperty("item").GetString().ShouldBe(item.GetProperty("key").GetString());
        (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "unlearned_reviews", source = "bot_review" })).GetProperty("reviews").EnumerateArray()
            .Single().GetProperty("external_id").GetString().ShouldBe("102");

        await RecordAsync(key, "Follow the store's lock order", "review", ["implementing"], "https://github.com/o/r/pull/7#discussion_r101");
        (await db.CallAsync(WorkTools.Lessons, new { project = key, view = "unlearned_reviews" })).GetProperty("reviews").EnumerateArray()
            .Single().GetProperty("external_id").GetString().ShouldBe("102");
        (await db.RefusedAsync(WorkTools.Lessons, new { project = key, view = "unlearned_reviews", activity = "testing" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task A_rule_superseded_by_a_new_wording_stays_a_rule_with_its_recurrence()
    {
        var key = await ProjectAsync("SS");
        var old = Id(await RecordAsync(key, "Rebase before merging", "review", ["merging"], "r1"));
        await RecordAsync(key, "Rebase before merging", "review", ["merging"], "r2");
        var current = await LessonAsync(old);
        current.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");

        var replacement = Id(await RecordAsync(key, "Rebase onto the target branch before merging", "agent", ["merging"],
            supersedes: new { id = old, expected_version = current.GetProperty("version").GetInt64() }));
        var lesson = (await LessonAsync(replacement)).GetProperty("lesson");
        lesson.GetProperty("state").GetString().ShouldBe("rule");
        lesson.GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(3);
        (await LessonAsync(old)).GetProperty("status").GetString().ShouldBe("superseded");
        RuleIds(await BriefingAsync(key, ["merging"])).ShouldBe([replacement]);
    }

    [Fact]
    public async Task Corrections_without_a_ref_in_one_session_on_two_items_are_two_events_and_challenges_are_capped_in_briefings()
    {
        var key = await ProjectAsync("SC");
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "claude-code" })).GetProperty("session").GetProperty("id").GetString();
        var correction = new { what = "Skipped the tests", correct_behavior = "Run the full test suite before review", applies_when = new { activity = new[] { "testing" } }, source = new { kind = "agent" } };
        string? lessonId = null;
        foreach (var title in new[] { "First", "Second" })
        {
            var item = await db.NewItemAsync(key, title);
            var updated = await db.CallAsync(WorkTools.UpdateWork, new
            {
                id = item.GetProperty("key").GetString(), expected_version = item.GetProperty("version").GetInt64(), session_id = session, corrections = new[] { correction }
            });
            lessonId ??= updated.GetProperty("recorded")[0].GetProperty("id").GetString();
        }

        var lesson = (await LessonAsync(lessonId!)).GetProperty("lesson");
        (lesson.GetProperty("recurrence").GetProperty("events").GetInt32(), lesson.GetProperty("state").GetString()).ShouldBe((2, "rule"));

        for (var index = 0; index < 5; index++)
            await db.CallAsync(WorkTools.Challenge, new { open = new { id = lessonId, reason = $"Reason {index}", evidence = new[] { new { kind = "link", summary = $"Evidence {index}" } } } });
        var rule = (await BriefingAsync(key, ["testing"])).GetProperty("rules").EnumerateArray().Single();
        (rule.GetProperty("challenges").GetArrayLength(), rule.GetProperty("more_challenges").GetInt32()).ShouldBe((LessonLimits.MaxShownChallenges, 2));
        var queued = (await db.CallAsync(WorkTools.ReviewQueue, new { project = key })).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "challenge");
        (queued.GetProperty("challenges").GetArrayLength(), queued.GetProperty("more_challenges").GetInt32()).ShouldBe((LessonLimits.MaxShownChallenges, 2));
    }

    [Fact]
    public async Task A_widened_only_redelivery_of_a_prompt_correction_does_not_promote_a_rejected_lesson_but_a_new_prompt_event_does()
    {
        var key = await ProjectAsync("SJ");
        var id = Id(await RecordAsync(key, "Squash before merging", "prompt", null, "prompt-1"));
        await db.CallAsync(WorkTools.Promote, new { id, expected_version = (await LessonAsync(id)).GetProperty("version").GetInt64(), action = "reject", reason = "Team prefers merge commits" });

        // The same prompt event again, now with an activity: widened, not counted, still rejected.
        var redelivered = await RecordAsync(key, "Squash before merging", "prompt", ["merging"], "prompt-1");
        Id(redelivered).ShouldBe(id);
        var lesson = await LessonAsync(id);
        (lesson.GetProperty("lesson").GetProperty("state").GetString(), lesson.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32()).ShouldBe(("rejected", 1));
        lesson.GetProperty("details").GetProperty("applies_when").GetProperty("activity").EnumerateArray().Single().GetString().ShouldBe("merging");
        RuleIds(await BriefingAsync(key, ["merging"])).ShouldBeEmpty();

        // A new correction from the human's prompt is a rule at once, over the rejection (R24).
        await RecordAsync(key, "Squash before merging", "prompt", ["merging"], "prompt-2");
        (await LessonAsync(id)).GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
    }

    [Fact]
    public async Task A_correction_without_a_ref_reported_on_an_item_and_again_at_end_session_without_one_counts_once()
    {
        var key = await ProjectAsync("SK");
        await db.NewItemAsync(key, "Fix the store");
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "claude-code" })).GetProperty("session").GetProperty("id").GetString();
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = session })).GetProperty("item");
        var correction = new { what = "Wrote the SQL by hand", correct_behavior = "Use the store's helpers for SQL", applies_when = new { activity = new[] { "implementing" } }, source = new { kind = "agent" } };
        // Reported on the item, then the item is let go of in the same call ...
        var updated = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(), expected_version = claimed.GetProperty("version").GetInt64(), session_id = session,
            corrections = new[] { correction }, release_claim = new { }
        });
        var lessonId = updated.GetProperty("recorded")[0].GetProperty("id").GetString()!;
        // ... and again at the end, which holds no item now.
        var ended = await db.CallAsync(WorkTools.EndSession, new { session_id = session, corrections = new[] { correction } });
        ended.GetProperty("recorded")[0].GetProperty("id").GetString().ShouldBe(lessonId);
        (await LessonAsync(lessonId)).GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(1);
    }

    private sealed class ZeroEmbedder : IEmbeddingService
    {
        // What OnnxEmbeddingService answers when inference fails: zero vectors of its dimension.
        public bool IsAvailable => true;
        public string ModelName => "zeros-1";
        public int Dimensions => 4;
        public float[] GenerateEmbedding(string text) => new float[Dimensions];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => texts.Select(_ => new float[Dimensions]).ToList();
    }

    [Fact]
    public async Task Zero_vectors_from_a_failing_provider_are_never_stored_or_scored_and_the_pair_falls_back_to_lexical()
    {
        var service = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }), embeddingService: new ZeroEmbedder());
        async Task<JsonElement> CallAsync(object arguments)
        {
            var result = await service.ExecuteAsync(WorkTools.Record, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor("alice"));
            return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
        }

        var key = await ProjectAsync("SZ");
        var first = Id(await CallAsync(new { project = key, entry = new { kind = "lesson", summary = "Run the tests before pushing", source = new { kind = "agent" } } }));
        var second = await CallAsync(new { project = key, entry = new { kind = "lesson", summary = "Run the test suite before pushing", source = new { kind = "agent" } } });
        var candidate = second.GetProperty("similar").EnumerateArray().Single().GetProperty("candidates").EnumerateArray().Single();
        (candidate.GetProperty("id").GetString(), candidate.GetProperty("method").GetString()).ShouldBe((first, "lexical"));

        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM lesson_embeddings WHERE model = 'zeros-1'")).ShouldBe(0);
    }

    [Fact]
    public async Task Pruning_a_session_keeps_its_served_rule_log_and_copies_its_attribution_onto_lesson_rows()
    {
        var key = await ProjectAsync("SP");
        var rule = Id(await RecordAsync(key, "Keep the lock order", "prompt", ["implementing"]));
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", model = "gpt-5", host = "box", activity = new[] { "implementing" } }, "carol");
        var session = started.GetProperty("session").GetProperty("id").GetString()!;
        RuleIds(started.GetProperty("briefing")).ShouldBe([rule]);
        await db.CallAsync(WorkTools.Record, new
        {
            session_id = session,
            entry = new { kind = "lesson", summary = "Keep the lock order", source = new { kind = "agent" }, applies_when = new { activity = new[] { "implementing" } }, same_as = rule }
        }, "carol");
        await db.CallAsync(WorkTools.EndSession, new { session_id = session }, "carol");

        await using var connection = new MySqlConnection(db.ConnectionString);
        var old = db.Clock.GetUtcNow().UtcDateTime.AddDays(-400);
        await connection.ExecuteAsync("UPDATE work_sessions SET last_seen_at = @old, ended_at = @old WHERE id = @session", new { session, old });
        await ((CodeGraph.Data.IWorkSessionPruner)db.Store).PruneSessionsAsync(db.Clock.GetUtcNow().UtcDateTime.AddDays(-300), 100);

        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM work_sessions WHERE id = @session", new { session })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM lesson_served WHERE session_id = @session", new { session })).ShouldBe(1);
        (await connection.ExecuteScalarAsync<string>("SELECT CONCAT(author_harness, '/', author_model, '/', author_host) FROM lesson_events WHERE session_id = @session", new { session }))
            .ShouldBe("codex/gpt-5/box");
        var lesson = (await LessonAsync(rule)).GetProperty("lesson");
        (lesson.GetProperty("served_sessions").GetInt32(), lesson.GetProperty("recurrence").GetProperty("while_served").GetInt32()).ShouldBe((1, 1));
    }
}
