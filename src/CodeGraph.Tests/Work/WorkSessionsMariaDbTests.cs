using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Sessions against a real MariaDB (sc-27866): two harnesses on one project see each other in their
/// briefings; a heartbeat lapse makes an in-progress item recoverable; a second session recovers it
/// with branch, PRs and the latest handoff (or the last change); the first session's next write is
/// claim_lost naming the new holder; and the briefing honours its budget and its project.
/// </summary>
public sealed class WorkSessionsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly TimeSpan PastIdle = TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5);

    private async Task<string> StartAsync(string project, string harness, string user = "michael", string? model = null, string? repository = null, string? branch = null, string? intent = null)
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness, model, host = "dev-mac", repository, branch, intent }, user);
        started.GetProperty("created").GetBoolean().ShouldBeTrue();
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> UpdateAsSessionAsync(string id, string session, object fields, string user = "michael")
    {
        var current = await db.CallAsync(WorkTools.GetWork, new { id });
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["session_id"] = session;
        arguments["expected_version"] = current.GetProperty("version").GetInt64();
        return (await db.CallAsync(WorkTools.UpdateWork, arguments, user)).GetProperty("item");
    }

    private async Task<WorkException> UpdateRefusedAsync(string id, string session, object fields, string user = "michael")
    {
        var current = await db.CallAsync(WorkTools.GetWork, new { id });
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["session_id"] = session;
        arguments["expected_version"] = current.GetProperty("version").GetInt64();
        return await db.RefusedAsync(WorkTools.UpdateWork, arguments, user);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() : null;

    [Fact]
    public async Task A_claude_code_and_a_codex_session_on_one_project_each_get_a_briefing_showing_the_other_live_session_and_its_claim()
    {
        var project = await db.NewProjectAsync("TW");
        var first = await db.NewItemAsync(project, "Port the kanban store");
        var second = await db.NewItemAsync(project, "Write the briefing");

        var claude = await StartAsync(project, "claude-code", model: "claude-opus", repository: "sceneworks", branch: "feature/a");
        var codex = await StartAsync(project, "codex", model: "gpt-5-codex", repository: "sceneworks", branch: "feature/b");

        var claudeItem = (await db.CallAsync(WorkTools.NextWork, new { project, session_id = claude }, "michael")).GetProperty("item");
        var codexItem = (await db.CallAsync(WorkTools.NextWork, new { project, session_id = codex }, "michael")).GetProperty("item");
        claudeItem.GetProperty("key").GetString().ShouldBe(first.GetProperty("key").GetString());
        codexItem.GetProperty("key").GetString().ShouldBe(second.GetProperty("key").GetString());
        // The claim is the session's: its id is the marker.
        claudeItem.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(claude);
        claudeItem.GetProperty("claim").GetProperty("marker").GetString().ShouldBe(claude);

        var claudeBrief = await db.CallAsync(WorkTools.GetBriefing, new { session_id = claude }, "michael");
        var codexBrief = await db.CallAsync(WorkTools.GetBriefing, new { session_id = codex }, "michael");

        var seenByClaude = claudeBrief.GetProperty("other_sessions").EnumerateArray().Single();
        seenByClaude.GetProperty("session_id").GetString().ShouldBe(codex);
        seenByClaude.GetProperty("harness").GetString().ShouldBe("codex");
        seenByClaude.GetProperty("holds").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(Text(codexItem, "key"));
        claudeBrief.GetProperty("my_work").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(Text(claudeItem, "key"));

        var seenByCodex = codexBrief.GetProperty("other_sessions").EnumerateArray().Single();
        seenByCodex.GetProperty("session_id").GetString().ShouldBe(claude);
        seenByCodex.GetProperty("harness").GetString().ShouldBe("claude-code");
        seenByCodex.GetProperty("holds").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(Text(claudeItem, "key"));

        // Every write is attributed to its session (harness, model, host) as well as the identity.
        var claims = (await db.AllChangesAsync(project)).Where(entry => entry.GetProperty("kind").GetString() == "claimed").ToList();
        claims.Select(entry => entry.GetProperty("actor").GetProperty("harness").GetString()).ShouldBe(["claude-code", "codex"]);
        claims.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("kind").GetString() == "session");
        claims[0].GetProperty("actor").GetProperty("session_id").GetString().ShouldBe(claude);
        claims[0].GetProperty("actor").GetProperty("model").GetString().ShouldBe("claude-opus");
        claims[0].GetProperty("actor").GetProperty("host").GetString().ShouldBe("dev-mac");
    }

    [Fact]
    public async Task A_killed_session_makes_its_item_recoverable_a_second_session_recovers_it_with_branch_and_handoff_and_the_first_gets_claim_lost_naming_the_new_holder()
    {
        var project = await db.NewProjectAsync("RC");
        var item = await db.NewItemAsync(project, "Recoverable work");
        var key = item.GetProperty("key").GetString()!;

        var first = await StartAsync(project, "claude-code", model: "claude-opus");
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = first }, "michael")).GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        await UpdateAsSessionAsync(key, first, new
        {
            status = "in_progress",
            add_links = new[] { new { kind = "branch", url = "https://github.com/o/r/tree/rc-1" }, new { kind = "pr", url = "https://github.com/o/r/pull/7" } },
            handoff = new { done = "Schema written.", open_issues = new[] { "Index name clashes with S3." }, next_action = "Write the store.", branch = "rc-1" }
        });

        // The session dies: no call for longer than the idle threshold.
        db.Clock.Advance(PastIdle);

        var second = await StartAsync(project, "codex", model: "gpt-5-codex", user: "bob");
        var recovered = await db.CallAsync(WorkTools.NextWork, new { project, session_id = second }, "bob");
        recovered.GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        recovered.GetProperty("item").GetProperty("status").GetString().ShouldBe("in_progress");
        recovered.GetProperty("item").GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(second);
        var recovery = recovered.GetProperty("recovery");
        recovery.GetProperty("previous_holder").GetProperty("session_id").GetString().ShouldBe(first);
        recovery.GetProperty("previous_holder").GetProperty("harness").GetString().ShouldBe("claude-code");
        recovery.GetProperty("branches").EnumerateArray().Single().GetString().ShouldBe("https://github.com/o/r/tree/rc-1");
        recovery.GetProperty("prs").EnumerateArray().Single().GetString().ShouldBe("https://github.com/o/r/pull/7");
        var handoff = recovery.GetProperty("latest_handoff");
        handoff.GetProperty("next_action").GetString().ShouldBe("Write the store.");
        handoff.GetProperty("open_issues").EnumerateArray().Single().GetString().ShouldBe("Index name clashes with S3.");
        handoff.GetProperty("session_id").GetString().ShouldBe(first);
        (await db.AllChangesAsync(project)).Last().GetProperty("kind").GetString().ShouldBe("recovered");

        // The first session comes back: its next write is refused claim_lost, naming the new holder.
        var lost = await UpdateRefusedAsync(key, first, new { comment = "Back again." });
        lost.Code.ShouldBe(WorkErrorCodes.ClaimLost);
        lost.Message.ShouldContain("bob");
        lost.Message.ShouldContain(second);
        lost.Message.ShouldContain("codex");
        lost.Current.ShouldBeOfType<ItemView>().Comments.ShouldBeEmpty();
        // It cannot take the item back while it is claimed.
        (await UpdateRefusedAsync(key, first, new { claim = new { } })).Code.ShouldBe(WorkErrorCodes.ClaimLost);

        // Letting go acknowledges the loss and changes nothing on the item; then its writes are ordinary.
        var released = await UpdateAsSessionAsync(key, first, new { release_claim = new { } });
        released.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(second);
        (await UpdateAsSessionAsync(key, first, new { comment = "Noted, bob has it." })).GetProperty("comments").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_session_that_died_without_a_handoff_hands_its_last_change_to_the_recoverer()
    {
        var project = await db.NewProjectAsync("LA");
        var key = (await db.NewItemAsync(project, "No handoff")).GetProperty("key").GetString()!;
        var first = await StartAsync(project, "claude-code");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = first }, "michael");
        await UpdateAsSessionAsync(key, first, new { status = "in_progress", description = "Half way: parser done." });

        db.Clock.Advance(PastIdle);
        var second = await StartAsync(project, "codex");
        var recovery = (await db.CallAsync(WorkTools.NextWork, new { project, session_id = second }, "michael")).GetProperty("recovery");

        recovery.TryGetProperty("latest_handoff", out _).ShouldBeFalse();
        var last = recovery.GetProperty("last_activity");
        last.GetProperty("actor").GetProperty("session_id").GetString().ShouldBe(first);
        last.GetProperty("changes").EnumerateArray().ShouldContain(change => change.GetProperty("field").GetString() == "description");
    }

    [Fact]
    public async Task A_returning_session_whose_item_stayed_unclaimed_is_told_unclaimed_and_may_reclaim_it()
    {
        var project = await db.NewProjectAsync("UC");
        var key = (await db.NewItemAsync(project, "Quiet")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, "claude-code");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "michael");
        await UpdateAsSessionAsync(key, session, new { status = "in_progress" });

        db.Clock.Advance(PastIdle);

        var lost = await UpdateRefusedAsync(key, session, new { status = "in_review" });
        lost.Code.ShouldBe(WorkErrorCodes.ClaimLost);
        lost.Message.ShouldContain("unclaimed");

        var reclaimed = await UpdateAsSessionAsync(key, session, new { claim = new { } });
        reclaimed.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(session);
        (await UpdateAsSessionAsync(key, session, new { status = "in_review" })).GetProperty("status").GetString().ShouldBe("in_review");
    }

    [Fact]
    public async Task A_live_session_keeps_its_claim_through_heartbeats_and_is_never_told_claim_lost()
    {
        var project = await db.NewProjectAsync("HB");
        var key = (await db.NewItemAsync(project, "Long work")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, "claude-code");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "michael");
        await UpdateAsSessionAsync(key, session, new { status = "in_progress" });

        // Each call inside the idle threshold renews the session and its claim.
        for (var beat = 0; beat < 4; beat++)
        {
            db.Clock.Advance(TimeSpan.FromSeconds(WorkDatabase.IdleSeconds - 60));
            await db.CallAsync(WorkTools.GetWork, new { id = key, session_id = session }, "michael");
        }

        var other = await StartAsync(project, "codex", user: "bob");
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = other }, "bob")).GetProperty("reason").GetString().ShouldBe("no_ready_work");
        (await UpdateAsSessionAsync(key, session, new { comment = "Still here." })).GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(session);
    }

    [Fact]
    public async Task Concurrent_recoverers_never_get_the_same_item()
    {
        var project = await db.NewProjectAsync("RR");
        var key = (await db.NewItemAsync(project, "One recoverable")).GetProperty("key").GetString()!;
        var dead = await StartAsync(project, "claude-code");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = dead }, "michael");
        await UpdateAsSessionAsync(key, dead, new { status = "in_progress" });
        db.Clock.Advance(PastIdle);

        var users = Enumerable.Range(0, 12).Select(index => $"recoverer-{index}").ToList();
        var sessions = new List<(string User, string Session)>();
        foreach (var user in users)
            sessions.Add((user, await StartAsync(project, "codex", user: user)));

        var results = await Task.WhenAll(sessions.Select(entry => db.CallAsync(WorkTools.NextWork, new { project, session_id = entry.Session }, entry.User)));

        results.Count(result => result.TryGetProperty("item", out _)).ShouldBe(1);
        results.Single(result => result.TryGetProperty("item", out _)).GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
    }

    [Fact]
    public async Task End_session_writes_the_handoff_releases_its_claims_and_the_next_session_recovers_with_it()
    {
        var project = await db.NewProjectAsync("ES");
        var key = (await db.NewItemAsync(project, "Handed over")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, "claude-code");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "michael");
        await UpdateAsSessionAsync(key, session, new { status = "in_progress" });

        var ended = await db.CallAsync(WorkTools.EndSession, new
        {
            session_id = session,
            handoff = new { done = "Half.", decisions = new[] { "Keep Dapper." }, corrections = new[] { "Use expected_version." }, next_action = "Finish.", commit = "abc123" }
        }, "michael");
        ended.GetProperty("session").GetProperty("live").GetBoolean().ShouldBeFalse();
        ended.GetProperty("released").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(key);
        ended.GetProperty("handoff").GetProperty("decisions").EnumerateArray().Single().GetString().ShouldBe("Keep Dapper.");

        var item = await db.CallAsync(WorkTools.GetWork, new { id = key });
        item.TryGetProperty("claim", out _).ShouldBeFalse();
        item.GetProperty("handoffs").EnumerateArray().Single().GetProperty("commit").GetString().ShouldBe("abc123");

        // A repeat answers the ended session and writes nothing again; the ended session cannot be used.
        var repeat = await db.CallAsync(WorkTools.EndSession, new { session_id = session, handoff = new { done = "Again." } }, "michael");
        repeat.GetProperty("released").GetArrayLength().ShouldBe(0);
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("handoffs").GetArrayLength().ShouldBe(1);
        (await db.RefusedAsync(WorkTools.GetBriefing, new { session_id = session }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var next = await StartAsync(project, "codex");
        var started = await db.CallAsync(WorkTools.GetBriefing, new { session_id = next }, "michael");
        var suggested = started.GetProperty("suggested_next");
        suggested.GetProperty("key").GetString().ShouldBe(key);
        suggested.GetProperty("recoverable").GetBoolean().ShouldBeTrue();
        suggested.GetProperty("latest_handoff").GetProperty("next_action").GetString().ShouldBe("Finish.");

        var recovered = await db.CallAsync(WorkTools.NextWork, new { project, session_id = next }, "michael");
        recovered.GetProperty("recovery").GetProperty("latest_handoff").GetProperty("done").GetString().ShouldBe("Half.");
        // The ended session no longer holds it, so it is not told claim_lost: it let go.
        (await UpdateAsSessionAsync(key, next, new { comment = "Taking over." })).GetProperty("comments").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_repeated_next_work_key_after_the_claim_passed_on_is_claim_lost_naming_the_holder()
    {
        var project = await db.NewProjectAsync("NK");
        var key = (await db.NewItemAsync(project, "Keyed")).GetProperty("key").GetString()!;
        var first = await StartAsync(project, "claude-code");
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = first, idempotency_key = "nk-1" }, "michael")).GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = first, idempotency_key = "nk-1" }, "michael")).GetProperty("reused").GetBoolean().ShouldBeTrue();
        await UpdateAsSessionAsync(key, first, new { status = "in_progress" });

        db.Clock.Advance(PastIdle);
        var second = await StartAsync(project, "codex", user: "bob");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = second }, "bob");

        var lost = await db.RefusedAsync(WorkTools.NextWork, new { project, session_id = first, idempotency_key = "nk-1" }, "michael");
        lost.Code.ShouldBe(WorkErrorCodes.ClaimLost);
        lost.Message.ShouldContain("bob");
    }

    [Fact]
    public async Task A_retried_claim_whose_answer_was_lost_answers_the_claim_the_session_already_holds()
    {
        var project = await db.NewProjectAsync("CR");
        var item = await db.NewItemAsync(project, "Claim me");
        var key = item.GetProperty("key").GetString()!;
        var version = item.GetProperty("version").GetInt64();
        var session = await StartAsync(project, "claude-code");

        var first = await db.CallAsync(WorkTools.UpdateWork, new { id = key, expected_version = version, session_id = session, claim = new { } }, "michael");
        // The same call again, with the version it read before the first one: not conflict.
        var retry = await db.CallAsync(WorkTools.UpdateWork, new { id = key, expected_version = version, session_id = session, claim = new { } }, "michael");

        retry.GetProperty("item").GetProperty("version").GetInt64().ShouldBe(first.GetProperty("item").GetProperty("version").GetInt64());
        retry.GetProperty("item").GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(session);
        // Another change with the stale version is still conflict.
        (await db.RefusedAsync(WorkTools.UpdateWork, new { id = key, expected_version = version, session_id = session, title = "x" }, "michael")).Code.ShouldBe(WorkErrorCodes.Conflict);
    }

    [Fact]
    public async Task Session_arguments_are_strict()
    {
        var project = await db.NewProjectAsync("SA");
        var key = (await db.NewItemAsync(project, "Strict")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, "claude-code");

        (await db.RefusedAsync(WorkTools.GetWork, new { id = key, session_id = session }, "mallory")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.GetWork, new { id = key, session_id = Guid.NewGuid().ToString() }, "michael")).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.GetWork, new { id = key, session_id = "not-a-session" }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var marker = await db.RefusedAsync(WorkTools.NextWork, new { project, session_id = session, marker = "m" }, "michael");
        marker.Message.ShouldContain("bound to the session");
        (await db.RefusedAsync(WorkTools.NextWork, new { project, session_id = session, ttl_seconds = 60 }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.NextWork, new { project }, "michael")).Message.ShouldContain("marker is required");
        // A session id passed as a marker, without session_id, would be a claim that only looks like the session's.
        (await db.RefusedAsync(WorkTools.NextWork, new { project, marker = session }, "michael")).Message.ShouldContain("pass it as session_id");
        var version = (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64();
        (await db.RefusedAsync(WorkTools.UpdateWork, new { id = key, expected_version = version, claim = new { marker = session } }, "michael")).Message.ShouldContain("pass it as session_id");
        var unknown = await db.RefusedAsync(WorkTools.StartSession, new { project, harness = "codex", modle = "x" }, "michael");
        unknown.Message.ShouldContain("modle");
        unknown.Message.ShouldContain("model");
        (await db.RefusedAsync(WorkTools.EndSession, new { session_id = session, handoff = new { } }, "michael")).Message.ShouldContain("empty");
        (await db.RefusedAsync(WorkTools.StartSession, new { harness = "codex" }, "michael")).Message.ShouldContain("project");
        (await db.RefusedAsync(WorkTools.GetBriefing, new { session_id = session, budget_tokens = 50 }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // A repeated start key answers the first session.
        var keyed = await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex", idempotency_key = "start-1" }, "michael");
        var again = await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex", idempotency_key = "start-1" }, "michael");
        again.GetProperty("created").GetBoolean().ShouldBeFalse();
        again.GetProperty("session").GetProperty("id").GetString().ShouldBe(keyed.GetProperty("session").GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_session_started_from_an_owned_repository_finds_its_project()
    {
        var project = await db.NewProjectAsync("OR", "mlx-rs");
        var started = await db.CallAsync(WorkTools.StartSession, new { harness = "codex", repository = "mlx-rs" }, "michael");
        started.GetProperty("session").GetProperty("project_key").GetString().ShouldBe(project);
        started.GetProperty("briefing").GetProperty("project").GetProperty("key").GetString().ShouldBe(project);
    }

    [Fact]
    public async Task The_briefing_stays_within_its_budget_omits_empty_sections_and_shows_only_its_own_project()
    {
        var quiet = await db.NewProjectAsync("BQ", "codegraph");
        var lonely = await db.CallAsync(WorkTools.StartSession, new { project = quiet, harness = "claude-code" }, "michael");
        var briefing = lonely.GetProperty("briefing");
        briefing.EnumerateObject().Select(property => property.Name).ShouldBe(["session_id", "budget_tokens", "estimated_tokens", "project"], ignoreOrder: true);

        var busy = await db.NewProjectAsync("BB", "inference");
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = busy,
            expected_version = (await db.CallAsync(WorkTools.GetWork, new { id = busy })).GetProperty("version").GetInt64(),
            goals = Enumerable.Range(1, 30).Select(index => $"Goal number {index}: {new string('g', 120)}").ToList()
        });
        for (var index = 0; index < 15; index++)
            await StartAsync(busy, "codex", user: $"peer-{index}", intent: new string('i', 600));
        var mine = await StartAsync(busy, "claude-code");

        var full = await db.CallAsync(WorkTools.GetBriefing, new { session_id = mine, budget_tokens = 20000 }, "michael");
        full.GetProperty("other_sessions").GetArrayLength().ShouldBe(15);
        full.TryGetProperty("truncated", out _).ShouldBeFalse();
        // No session of another project leaks in.
        full.GetProperty("other_sessions").EnumerateArray().ShouldAllBe(other => other.GetProperty("identity").GetString()!.StartsWith("peer-"));

        foreach (var budget in new[] { 200, 600, 1500 })
        {
            var cut = await db.CallAsync(WorkTools.GetBriefing, new { session_id = mine, budget_tokens = budget }, "michael");
            cut.GetProperty("estimated_tokens").GetInt32().ShouldBeLessThanOrEqualTo(budget);
            WorkBriefings.Estimate(cut.Deserialize<BriefingView>(WorkJson.Options)!).ShouldBeLessThanOrEqualTo(budget);
            System.Text.Encoding.UTF8.GetByteCount(cut.GetRawText()).ShouldBeLessThanOrEqualTo(budget * 3);
            cut.GetProperty("truncated").EnumerateArray().Select(name => name.GetString()).ShouldContain("other_sessions");
        }
    }
}
