using System.Text.Json;
using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The REST calls the Knowledge, Lessons and Review queue pages make (sc-27875, E8): the UI writes
/// only through the routes agents use, so these drive those routes with the exact bodies the pages
/// send and then read the next briefing, as an agent would.
/// </summary>
public sealed class KnowledgeUiRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service);
        token = await host.CreateTokenAsync("ui-reviewer");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<(int Status, JsonElement Body)> Call(HttpMethod method, string path, object? body = null, string? query = null) =>
        await host.SendAsync(method, path + query, body is null ? null : JsonSerializer.Serialize(body, WorkJson.Options), token);

    private async Task<string> NewProjectAsync(string prefix)
    {
        var key = prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        (await Call(HttpMethod.Post, "/api/work", new { new_project = new { key, name = "Knowledge UI" } })).Status.ShouldBe(200);
        return key;
    }

    private async Task<JsonElement> RecordRuleAsync(string key, string summary, string activity)
    {
        var (status, recorded) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            idempotency_key = "ui-" + Guid.NewGuid().ToString("N"),
            entry = new { kind = "lesson", summary, source = new { kind = "prompt" }, applies_when = new { activity = new[] { activity } } }
        });
        status.ShouldBe(200);
        return recorded.GetProperty("entry");
    }

    private async Task<JsonElement> BriefingAsync(string session, string activity)
    {
        var (status, briefing) = await Call(HttpMethod.Get, $"/api/work/sessions/{session}/briefing", query: $"?activity={activity}");
        status.ShouldBe(200);
        return briefing;
    }

    private static IEnumerable<JsonElement> Rules(JsonElement briefing) =>
        briefing.TryGetProperty("rules", out var rules) ? rules.EnumerateArray() : [];

    private async Task<string> StartSessionAsync(string key)
    {
        var (_, started) = await Call(HttpMethod.Post, "/api/work/sessions", new { project = key, harness = "claude-code", model = "opus", host = "dev-mac" });
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Retracting_a_recently_learned_rule_from_the_ui_takes_it_out_of_the_next_briefing()
    {
        var key = await NewProjectAsync("KU");
        var rule = await RecordRuleAsync(key, "Never force-push a shared branch", "merging");
        var ruleId = rule.GetProperty("id").GetString();
        var session = await StartSessionAsync(key);
        Rules(await BriefingAsync(session, "merging")).Select(served => served.GetProperty("id").GetString()).ShouldBe([ruleId]);

        // The Lessons page's "Recently learned" list, with who recorded each rule (E3).
        var (_, learned) = await Call(HttpMethod.Post, "/api/work/knowledge/lessons", new { project = key, view = "recently_learned", limit = 20 });
        var row = learned.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("id").GetString().ShouldBe(ruleId);
        row.GetProperty("author").GetProperty("name").GetString().ShouldBe("ui-reviewer");

        // The page's retract: record retract with the version it read.
        var (retractStatus, retracted) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            retract = new { id = ruleId, expected_version = row.GetProperty("version").GetInt64(), reason = "Too broad: release branches are rebased" }
        });
        retractStatus.ShouldBe(200);
        retracted.GetProperty("entry").GetProperty("status").GetString().ShouldBe("retracted");

        Rules(await BriefingAsync(session, "merging")).ShouldBeEmpty();
        var (_, after) = await Call(HttpMethod.Post, "/api/work/knowledge/lessons", new { project = key, view = "recently_learned", limit = 20 });
        after.GetProperty("items").EnumerateArray().ShouldBeEmpty();

        // A stale version (a second click with the old version) is refused, not applied twice.
        var (staleStatus, stale) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            retract = new { id = ruleId, expected_version = row.GetProperty("version").GetInt64(), reason = "again" }
        });
        (staleStatus, stale.GetProperty("code").GetString()).ShouldBe((409, "conflict"));
    }

    [Fact]
    public async Task Resolving_a_challenge_from_the_ui_is_reflected_in_the_next_briefing()
    {
        var key = await NewProjectAsync("KC");
        var amended = await RecordRuleAsync(key, "Run the whole suite before review", "reviewing");
        var upheld = await RecordRuleAsync(key, "Link the PR on the item", "reviewing");
        var retracted = await RecordRuleAsync(key, "Never squash", "reviewing");
        var session = await StartSessionAsync(key);

        foreach (var rule in new[] { amended, upheld, retracted })
        {
            var (openStatus, _) = await Call(HttpMethod.Post, "/api/work/knowledge/challenges", new
            {
                idempotency_key = "ui-" + Guid.NewGuid().ToString("N"),
                open = new { id = rule.GetProperty("id").GetString(), reason = "Counter-example", evidence = new[] { new { kind = "counter_example", summary = "CI already runs it" } } }
            });
            openStatus.ShouldBe(200);
        }

        var before = Rules(await BriefingAsync(session, "reviewing")).ToList();
        before.Count.ShouldBe(3);
        before.ShouldAllBe(served => served.GetProperty("challenges").GetArrayLength() == 1);

        // The review queue's challenge items: one per rule, each with its challenge, its author and the rule's version.
        var (_, queue) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=50");
        var items = queue.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        items.ShouldAllBe(item => item.GetProperty("kind").GetString() == "challenge");
        items.ShouldAllBe(item => item.GetProperty("challenges")[0].GetProperty("author").GetProperty("name").GetString() == "ui-reviewer");

        async Task Resolve(object resolution)
        {
            var (status, resolved) = await Call(HttpMethod.Post, "/api/work/knowledge/challenges", new { resolve = resolution });
            status.ShouldBe(200);
            resolved.GetProperty("challenge").GetProperty("status").GetString().ShouldNotBe("open");
        }

        string ChallengeOf(JsonElement rule) => items.Single(item => item.GetProperty("entry").GetProperty("id").GetString() == rule.GetProperty("id").GetString())
            .GetProperty("challenges")[0].GetProperty("id").GetString()!;
        long VersionOf(JsonElement rule) => items.Single(item => item.GetProperty("entry").GetProperty("id").GetString() == rule.GetProperty("id").GetString())
            .GetProperty("entry").GetProperty("version").GetInt64();

        await Resolve(new
        {
            challenge_id = ChallengeOf(amended), expected_version = VersionOf(amended), outcome = "amend", reason = "Only the affected projects",
            amend = new { summary = "Run the affected test projects before review" }
        });
        await Resolve(new { challenge_id = ChallengeOf(upheld), expected_version = VersionOf(upheld), outcome = "uphold", reason = "Still needed" });
        await Resolve(new { challenge_id = ChallengeOf(retracted), expected_version = VersionOf(retracted), outcome = "retract", reason = "Wrong" });

        var after = Rules(await BriefingAsync(session, "reviewing")).ToList();
        after.Select(served => served.GetProperty("id").GetString()).Order()
            .ShouldBe(new[] { amended.GetProperty("id").GetString(), upheld.GetProperty("id").GetString() }.Order());
        after.Single(served => served.GetProperty("id").GetString() == amended.GetProperty("id").GetString())
            .GetProperty("summary").GetString().ShouldBe("Run the affected test projects before review");
        after.Where(served => served.TryGetProperty("challenges", out var open) && open.GetArrayLength() > 0).ShouldBeEmpty();

        var (_, emptied) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=50");
        emptied.GetProperty("items").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_review_queue_lists_every_kind_with_authors_and_settles_from_the_ui_without_blocking_work()
    {
        var key = await NewProjectAsync("KQ");
        var rule = await RecordRuleAsync(key, "Never push to main", "merging");
        (await Call(HttpMethod.Post, "/api/work/knowledge/challenges", new
        {
            idempotency_key = "ui-challenge-1",
            open = new { id = rule.GetProperty("id").GetString(), reason = "Hotfixes", evidence = new[] { new { kind = "link", summary = "HOTFIX.md" } } }
        })).Status.ShouldBe(200);

        var (_, candidate) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key, idempotency_key = "ui-candidate-1",
            entry = new { kind = "lesson", summary = "Squash fixups before review", source = new { kind = "agent" }, applies_when = new { activity = new[] { "reviewing" } } }
        });
        var proposed = candidate.GetProperty("entry");
        (await Call(HttpMethod.Post, $"/api/work/knowledge/{proposed.GetProperty("id").GetString()}/promote",
            new { expected_version = proposed.GetProperty("version").GetInt64(), action = "propose" })).Status.ShouldBe(200);

        var (_, contradicting) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key, idempotency_key = "ui-contradiction-1",
            entry = new { kind = "lesson", summary = "Push hotfixes to main directly", source = new { kind = "agent" }, applies_when = new { activity = new[] { "merging" } },
                contradicts = rule.GetProperty("id").GetString() }
        });
        contradicting.GetProperty("entry").GetProperty("id").GetString().ShouldNotBeNull();

        var (_, decision) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key, idempotency_key = "ui-decision-1",
            entry = new { kind = "decision", summary = "Use SSE for the message stream", why = "One-way", made_autonomously = true }
        });
        var decisionEntry = decision.GetProperty("entry");

        // Work goes on while the queue is full (E6): an item is claimed as usual.
        (await Call(HttpMethod.Post, "/api/work", new { project = key, item = new { title = "Ship it", status = "ready" } })).Status.ShouldBe(200);
        var session = await StartSessionAsync(key);
        var (nextStatus, next) = await Call(HttpMethod.Post, "/api/work/next", new { project = key, session_id = session });
        nextStatus.ShouldBe(200);
        next.GetProperty("item").GetProperty("title").GetString().ShouldBe("Ship it");

        var (queueStatus, queue) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=50");
        queueStatus.ShouldBe(200);
        var items = queue.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("kind").GetString()).Order()
            .ShouldBe(new[] { "autonomous_decision", "challenge", "contradiction", "proposed_rule" }.Order());
        items.ShouldAllBe(item => item.GetProperty("author").GetProperty("name").GetString() == "ui-reviewer");
        items.Single(item => item.GetProperty("kind").GetString() == "contradiction").GetProperty("other_author").GetProperty("name").GetString().ShouldBe("ui-reviewer");

        // A limit out of range is refused, not clamped; an unknown parameter is refused.
        (await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=51")).Status.ShouldBe(400);
        var (unknownStatus, unknown) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&kind=challenge");
        (unknownStatus, unknown.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));

        // One click each: approve the proposed rule, acknowledge the decision.
        (await Call(HttpMethod.Post, $"/api/work/knowledge/{proposed.GetProperty("id").GetString()}/promote",
            new { expected_version = items.Single(item => item.GetProperty("kind").GetString() == "proposed_rule").GetProperty("entry").GetProperty("version").GetInt64(), action = "approve" }))
            .Status.ShouldBe(200);
        (await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            acknowledge = new { id = decisionEntry.GetProperty("id").GetString(), expected_version = decisionEntry.GetProperty("version").GetInt64() }
        })).Status.ShouldBe(200);

        // The acknowledged decision says so (the Knowledge page hides Acknowledge on it).
        var (_, acknowledged) = await Call(HttpMethod.Get, $"/api/work/knowledge/{decisionEntry.GetProperty("id").GetString()}");
        acknowledged.GetProperty("items")[0].TryGetProperty("reviewed_at", out _).ShouldBeTrue();
        decisionEntry.TryGetProperty("reviewed_at", out _).ShouldBeFalse();

        var (_, settled) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=50");
        settled.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).Order()
            .ShouldBe(new[] { "challenge", "contradiction" }.Order());

        // Reject with a reason: a fresh candidate proposed then rejected leaves the queue with the reason recorded.
        var (_, other) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key, idempotency_key = "ui-candidate-2",
            entry = new { kind = "lesson", summary = "Rebase every morning", source = new { kind = "agent" }, applies_when = new { activity = new[] { "coordinating" } } }
        });
        var otherEntry = other.GetProperty("entry");
        var (_, proposedAgain) = await Call(HttpMethod.Post, $"/api/work/knowledge/{otherEntry.GetProperty("id").GetString()}/promote",
            new { expected_version = otherEntry.GetProperty("version").GetInt64(), action = "propose" });
        var (rejectStatus, rejected) = await Call(HttpMethod.Post, $"/api/work/knowledge/{otherEntry.GetProperty("id").GetString()}/promote",
            new { expected_version = proposedAgain.GetProperty("entry").GetProperty("version").GetInt64(), action = "reject", reason = "Too noisy" });
        rejectStatus.ShouldBe(200);
        rejected.GetProperty("entry").GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rejected");
        rejected.GetProperty("entry").GetProperty("lesson").GetProperty("rejected_reason").GetString().ShouldBe("Too noisy");
    }
}
