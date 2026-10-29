using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Resource holds against a real MariaDB (sc-27872): lifecycle with the shiftable clock (a hold
/// outlives its silent session and reads owner unreachable, never available; it frees on release,
/// end_session or its own ttl; a renewal after expiry is refused, never a silent regrant), capacity
/// accounting under concurrent acquires, FIFO with priority, and waiters of dead sessions dropped.
/// </summary>
public sealed class WorkHoldsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly TimeSpan PastIdle = TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5);

    private async Task<string> StartAsync(string project, string harness = "claude-code", string user = "michael")
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness, host = "gpu-box" }, user);
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private Task<JsonElement> AcquireAsync(string project, string resource, string user = "michael", string? session = null, int ttl = 3600, string purpose = "training run",
        int? wait = null, string? priority = null, int? capacity = null, string? item = null, string? key = null, string[]? labels = null) =>
        db.CallAsync(WorkTools.Acquire, new Dictionary<string, object?>
        {
            ["project"] = session is null ? project : null,
            ["session_id"] = session,
            ["resource"] = resource,
            ["ttl_seconds"] = ttl,
            ["purpose"] = purpose,
            ["wait_seconds"] = wait,
            ["priority"] = priority,
            ["capacity"] = capacity,
            ["item"] = item,
            ["idempotency_key"] = key,
            ["labels"] = labels
        }, user);

    private Task<JsonElement> RenewAsync(string holdId, string user = "michael", int? ttl = null) =>
        db.CallAsync(WorkTools.RenewHold, ttl is null ? new { hold_id = holdId } : (object)new { hold_id = holdId, ttl_seconds = ttl }, user);

    private Task<JsonElement> ReleaseAsync(string holdId, string user = "michael") =>
        db.CallAsync(WorkTools.Release, new { hold_id = holdId }, user);

    private async Task WaitForWaitersAsync(string project, string resource, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var probe = await AcquireAsync(project, resource, user: "probe");
            if (probe.GetProperty("resource").TryGetProperty("waiters", out var waiters) && waiters.GetArrayLength() == count)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"{count} waiters never queued on {resource}.");
    }

    private static string HoldId(JsonElement result) => result.GetProperty("hold").GetProperty("id").GetString()!;

    private static DateTime At(JsonElement hold, string property) => hold.GetProperty(property).GetDateTime();

    [Fact]
    public async Task The_first_acquire_creates_the_resource_and_grants_a_hold_a_second_caller_is_told_who_holds_it()
    {
        var project = await db.NewProjectAsync("HA");
        var first = await AcquireAsync(project, "gpu:4090-box", user: "alice", purpose: "LoRA training", labels: ["cuda", "24gb"]);

        first.GetProperty("outcome").GetString().ShouldBe("granted");
        first.GetProperty("resource_created").GetBoolean().ShouldBeTrue();
        var hold = first.GetProperty("hold");
        (hold.GetProperty("state").GetString(), hold.GetProperty("purpose").GetString(), hold.GetProperty("ttl_seconds").GetInt32()).ShouldBe(("held", "LoRA training", 3600));
        (At(hold, "expires_at") - At(hold, "granted_at")).ShouldBe(TimeSpan.FromHours(1));
        var resource = first.GetProperty("resource");
        (resource.GetProperty("capacity").GetInt32(), resource.GetProperty("used").GetInt32(), resource.GetProperty("available").GetBoolean()).ShouldBe((1, 1, false));
        resource.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ShouldBe(["cuda", "24gb"]);

        // Names compare ignoring case; the second caller gets the holders, their purpose and expiry.
        var second = await AcquireAsync(project, "GPU:4090-BOX", user: "bob", purpose: "inference");
        second.GetProperty("outcome").GetString().ShouldBe("held");
        second.TryGetProperty("hold", out _).ShouldBeFalse();
        var holder = second.GetProperty("resource").GetProperty("holders").EnumerateArray().Single();
        (holder.GetProperty("purpose").GetString(), holder.GetProperty("holder").GetProperty("identity").GetString()).ShouldBe(("LoRA training", "alice"));
        At(holder, "expires_at").ShouldBe(At(hold, "expires_at"));
        second.GetProperty("resource").GetProperty("available").GetBoolean().ShouldBeFalse();
        second.TryGetProperty("resource_created", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_hold_renewed_with_its_own_calls_survives_forty_minutes_of_session_silence_and_reads_owner_unreachable_never_available()
    {
        var project = await db.NewProjectAsync("HL");
        var sleeper = await StartAsync(project);
        var watcher = await StartAsync(project, "codex");
        var acquired = await AcquireAsync(project, "gpu:long", session: sleeper, ttl: 1200, purpose: "30 minute render");
        var holdId = HoldId(acquired);

        var lastSeen = (await db.CallAsync(WorkTools.GetBriefing, new { session_id = watcher }, "michael")).GetProperty("other_sessions").EnumerateArray().Single()
            .GetProperty("last_seen_at").GetDateTime();

        for (var minutes = 10; minutes <= 40; minutes += 10)
        {
            db.Clock.Advance(TimeSpan.FromMinutes(10));
            // The sleeper's session makes no call at all. Its hold is renewed on its own (cg hold
            // renew: the identity's token, no session id).
            var renewed = await RenewAsync(holdId);
            renewed.GetProperty("state").GetString().ShouldBe("held");

            // Another session (kept alive by its own calls) looks at the resource.
            var seen = await AcquireAsync(project, "gpu:long", session: watcher, purpose: "inference");
            seen.GetProperty("outcome").GetString().ShouldBe("held");
            seen.GetProperty("resource").GetProperty("available").GetBoolean().ShouldBeFalse();
            var holder = seen.GetProperty("resource").GetProperty("holders").EnumerateArray().Single();
            holder.GetProperty("state").GetString().ShouldBe("held");
            At(holder, "expires_at").ShouldBeGreaterThan(At(holder, "granted_at"));
            if (minutes >= 20)
            {
                // Silent past the 15 minute threshold: still held, said so, with since.
                holder.GetProperty("owner_unreachable").GetBoolean().ShouldBeTrue();
                At(holder, "owner_unreachable_since").ShouldBe(lastSeen.AddSeconds(WorkDatabase.IdleSeconds), TimeSpan.FromSeconds(1));
                At(holder, "owner_last_seen_at").ShouldBe(lastSeen, TimeSpan.FromSeconds(1));
            }

            // The watcher's briefing says the same.
            var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = watcher }, "michael");
            var resource = briefing.GetProperty("resources").EnumerateArray().Single();
            resource.GetProperty("name").GetString().ShouldBe("gpu:long");
            resource.GetProperty("available").GetBoolean().ShouldBeFalse();
            if (minutes >= 20)
            {
                resource.GetProperty("holders").EnumerateArray().Single().GetProperty("owner_unreachable").GetBoolean().ShouldBeTrue();
                // The silent session is no longer listed as live, yet its hold is.
                briefing.TryGetProperty("other_sessions", out _).ShouldBeFalse();
            }
        }

        // It frees on release.
        var released = await ReleaseAsync(holdId);
        released.GetProperty("hold").GetProperty("state").GetString().ShouldBe("released");
        released.GetProperty("resource").GetProperty("available").GetBoolean().ShouldBeTrue();
        (await AcquireAsync(project, "gpu:long", session: watcher, purpose: "inference")).GetProperty("outcome").GetString().ShouldBe("granted");
    }

    [Fact]
    public async Task A_hold_that_is_not_renewed_frees_at_its_ttl_and_a_late_renewal_is_refused_never_a_regrant()
    {
        var project = await db.NewProjectAsync("HT");
        var first = await AcquireAsync(project, "runner:dev-mac", user: "alice", ttl: 60);
        var holdId = HoldId(first);

        db.Clock.Advance(TimeSpan.FromSeconds(59));
        (await AcquireAsync(project, "runner:dev-mac", user: "bob")).GetProperty("outcome").GetString().ShouldBe("held");

        db.Clock.Advance(TimeSpan.FromSeconds(2));
        // Expired: another caller gets the slot ...
        var taken = await AcquireAsync(project, "runner:dev-mac", user: "bob");
        taken.GetProperty("outcome").GetString().ShouldBe("granted");

        // ... and the late renewal is refused with the hold as it is, not a silent regrant.
        var refused = await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = holdId }, "alice");
        refused.Code.ShouldBe(WorkErrorCodes.Conflict);
        refused.Message.ShouldContain("lapsed");
        JsonSerializer.SerializeToElement(refused.Current, WorkJson.Options).GetProperty("state").GetString().ShouldBe("expired");

        // The slot is still bob's; releasing the expired hold changes nothing and answers it.
        var released = await ReleaseAsync(holdId, "alice");
        released.GetProperty("hold").GetProperty("state").GetString().ShouldBe("expired");
        released.GetProperty("resource").GetProperty("holders").EnumerateArray().Single().GetProperty("holder").GetProperty("identity").GetString().ShouldBe("bob");

        // With nobody else it still must be acquired again: one renewal after expiry also never revives it.
        await ReleaseAsync(HoldId(taken), "bob");
        (await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = holdId }, "alice")).Code.ShouldBe(WorkErrorCodes.Conflict);
        (await AcquireAsync(project, "runner:dev-mac", user: "alice")).GetProperty("outcome").GetString().ShouldBe("granted");
    }

    [Fact]
    public async Task A_renewal_moves_the_expiry_by_its_ttl_and_only_the_holder_may_renew_or_release()
    {
        var project = await db.NewProjectAsync("HR");
        var holdId = HoldId(await AcquireAsync(project, "db:staging", user: "alice", ttl: 300));

        db.Clock.Advance(TimeSpan.FromSeconds(200));
        var renewed = await RenewAsync(holdId, "alice", ttl: 900);
        var expires = At(renewed, "expires_at");
        (expires - At(renewed, "renewed_at")).ShouldBe(TimeSpan.FromSeconds(900));
        renewed.GetProperty("ttl_seconds").GetInt32().ShouldBe(900);

        // Without a ttl the hold's own is used.
        db.Clock.Advance(TimeSpan.FromSeconds(100));
        var again = await RenewAsync(holdId, "alice");
        (At(again, "expires_at") - At(again, "renewed_at")).ShouldBe(TimeSpan.FromSeconds(900));

        foreach (var tool in new[] { WorkTools.RenewHold, WorkTools.Release })
        {
            var refused = await db.RefusedAsync(tool, new { hold_id = holdId }, "mallory");
            // Not revealed to a stranger: it is simply not found.
            refused.Code.ShouldBe(WorkErrorCodes.NotFound);
        }

        (await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = Guid.NewGuid().ToString() }, "alice")).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = "nope" }, "alice")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task A_renewal_is_not_a_session_heartbeat_but_acquire_with_a_session_is()
    {
        var project = await db.NewProjectAsync("HB");
        var session = await StartAsync(project);
        var holdId = HoldId(await AcquireAsync(project, "gpu:hb", session: session, ttl: 7200));

        db.Clock.Advance(PastIdle);
        await RenewAsync(holdId);
        (await ReleaseAsync(holdId)).GetProperty("hold").GetProperty("state").GetString().ShouldBe("released");

        // The session stayed silent through the renewal and the release: it is not live. Its own
        // acquire is a heartbeat, so it is live again after one.
        (await db.Store.GetSessionAsync(Guid.Parse(session)))!.Live.ShouldBeFalse();
        await AcquireAsync(project, "gpu:hb2", session: session);
        (await db.Store.GetSessionAsync(Guid.Parse(session)))!.Live.ShouldBeTrue();
    }

    [Fact]
    public async Task End_session_releases_the_session_holds_and_drops_its_waiting_entries()
    {
        var project = await db.NewProjectAsync("HE");
        var session = await StartAsync(project);
        var holdId = HoldId(await AcquireAsync(project, "gpu:end", session: session));
        await AcquireAsync(project, "gpu:other", user: "bob");
        var queued = await AcquireAsync(project, "gpu:other", session: session, wait: 1);
        queued.GetProperty("hold").GetProperty("state").GetString().ShouldBe("waiting");
        queued.GetProperty("hold").GetProperty("queue_position").GetInt32().ShouldBe(1);

        // A hold is its session's until the session ends: silence alone frees nothing.
        db.Clock.Advance(PastIdle);
        (await AcquireAsync(project, "gpu:end", user: "bob")).GetProperty("outcome").GetString().ShouldBe("held");

        var ended = await db.CallAsync(WorkTools.EndSession, new { session_id = session }, "michael");
        var releasedHolds = ended.GetProperty("released_holds").EnumerateArray().ToList();
        releasedHolds.Select(hold => (hold.GetProperty("resource").GetString(), hold.GetProperty("state").GetString()))
            .ShouldBe([("gpu:end", "released"), ("gpu:other", "cancelled")], ignoreOrder: true);
        releasedHolds.First(hold => hold.GetProperty("resource").GetString() == "gpu:end").GetProperty("id").GetString().ShouldBe(holdId);

        (await AcquireAsync(project, "gpu:end", user: "bob")).GetProperty("outcome").GetString().ShouldBe("granted");
    }

    [Fact]
    public async Task End_session_stamps_a_lapsed_unswept_hold_ended_at_its_expiry_and_does_not_report_it_released()
    {
        var project = await db.NewProjectAsync("HY");
        var session = await StartAsync(project);
        var holdId = HoldId(await AcquireAsync(project, "gpu:lapsed", session: session, ttl: 60));
        db.Clock.Advance(TimeSpan.FromSeconds(300));

        var ended = await db.CallAsync(WorkTools.EndSession, new { session_id = session }, "michael");
        ended.TryGetProperty("released_holds", out _).ShouldBeFalse();
        await using var connection = new MySqlConnection(db.ConnectionString);
        var row = await connection.QuerySingleAsync<(string State, DateTime EndedAt, DateTime ExpiresAt)>(
            "SELECT state, ended_at, expires_at FROM work_holds WHERE id = @holdId", new { holdId });
        (row.State, row.EndedAt).ShouldBe(("expired", row.ExpiresAt));
    }

    [Fact]
    public async Task Concurrent_acquires_never_exceed_capacity()
    {
        var project = await db.NewProjectAsync("HC");
        await AcquireAsync(project, "gpu:pool", user: "creator", capacity: 3, purpose: "creates the pool");
        // The creator holds one slot of three; 14 more callers race for the other two.
        var racers = Enumerable.Range(0, 14).Select(index => AcquireAsync(project, "gpu:pool", user: $"racer{index}", purpose: "race")).ToList();
        var results = await Task.WhenAll(racers);

        results.Count(result => result.GetProperty("outcome").GetString() == "granted").ShouldBe(2);
        results.Count(result => result.GetProperty("outcome").GetString() == "held").ShouldBe(12);
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM work_holds h JOIN work_resources r ON r.id = h.resource_id WHERE r.name = 'gpu:pool' AND r.project_id = (SELECT id FROM work_projects WHERE project_key = @project) AND h.state = 'held'",
            new { project })).ShouldBe(3);
    }

    [Fact]
    public async Task One_idempotency_key_raced_onto_two_resources_is_refused_for_one_of_them_not_a_server_error()
    {
        var project = await db.NewProjectAsync("HZ");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            Task.Run(async () =>
            {
                try
                {
                    return (await AcquireAsync(project, $"gpu:key{index % 2}", user: "alice", key: "same")).GetProperty("outcome").GetString();
                }
                catch (WorkException exception)
                {
                    return exception.Code;
                }
            })));
        attempts.ShouldAllBe(code => code == "granted" || code == "held" || code == WorkErrorCodes.InvalidArgument);
        attempts.ShouldContain("granted");
    }

    [Fact]
    public async Task Concurrent_first_acquires_create_one_resource_and_grant_exactly_one_hold()
    {
        var project = await db.NewProjectAsync("HF");
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => AcquireAsync(project, "gpu:new", user: $"u{index}")));

        results.Count(result => result.GetProperty("outcome").GetString() == "granted").ShouldBe(1);
        results.Count(result => result.TryGetProperty("resource_created", out _)).ShouldBe(1);
        results.Select(result => result.GetProperty("resource").GetProperty("id").GetString()).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Waiters_are_served_by_priority_then_first_come_first_served_when_a_slot_frees()
    {
        var project = await db.NewProjectAsync("HQ");
        var holder = HoldId(await AcquireAsync(project, "gpu:q", user: "owner"));

        var medium1 = AcquireAsync(project, "gpu:q", user: "m1", wait: 8, ttl: 600, purpose: "first medium");
        await WaitForWaitersAsync(project, "gpu:q", 1);
        var medium2 = AcquireAsync(project, "gpu:q", user: "m2", wait: 8, ttl: 600, purpose: "second medium");
        await WaitForWaitersAsync(project, "gpu:q", 2);
        var critical = AcquireAsync(project, "gpu:q", user: "c1", wait: 8, ttl: 600, priority: "critical", purpose: "urgent");
        await WaitForWaitersAsync(project, "gpu:q", 3);

        var probe = await AcquireAsync(project, "gpu:q", user: "probe");
        probe.GetProperty("resource").GetProperty("waiters").EnumerateArray()
            .Select(waiter => (waiter.GetProperty("holder").GetProperty("identity").GetString(), waiter.GetProperty("queue_position").GetInt32()))
            .ShouldBe([("c1", 1), ("m1", 2), ("m2", 3)]);

        await ReleaseAsync(holder, "owner");
        var firstOut = await critical;
        firstOut.GetProperty("outcome").GetString().ShouldBe("granted");
        firstOut.GetProperty("hold").GetProperty("purpose").GetString().ShouldBe("urgent");
        (await AcquireAsync(project, "gpu:q", user: "probe")).GetProperty("resource").GetProperty("waiters").GetArrayLength().ShouldBe(2);

        await ReleaseAsync(HoldId(firstOut), "c1");
        var secondOut = await medium1;
        secondOut.GetProperty("outcome").GetString().ShouldBe("granted");
        await ReleaseAsync(HoldId(secondOut), "m1");
        (await medium2).GetProperty("outcome").GetString().ShouldBe("granted");
    }

    [Fact]
    public async Task A_waiter_that_polls_keeps_its_place_and_learns_it_was_granted()
    {
        var project = await db.NewProjectAsync("HP");
        var holder = HoldId(await AcquireAsync(project, "gpu:poll", user: "owner"));

        var waiting = await AcquireAsync(project, "gpu:poll", user: "w1", wait: 1);
        waiting.GetProperty("outcome").GetString().ShouldBe("held");
        var place = waiting.GetProperty("hold");
        (place.GetProperty("state").GetString(), place.GetProperty("queue_position").GetInt32()).ShouldBe(("waiting", 1));
        // A later arrival queues behind it.
        (await AcquireAsync(project, "gpu:poll", user: "w2", wait: 1)).GetProperty("hold").GetProperty("queue_position").GetInt32().ShouldBe(2);

        await ReleaseAsync(holder, "owner");
        // The slot went to the front of the queue at the release; w2 cannot take it by asking.
        (await AcquireAsync(project, "gpu:poll", user: "w2")).GetProperty("outcome").GetString().ShouldBe("held");
        var granted = await AcquireAsync(project, "gpu:poll", user: "w1");
        granted.GetProperty("outcome").GetString().ShouldBe("granted");
        granted.GetProperty("hold").GetProperty("id").GetString().ShouldBe(place.GetProperty("id").GetString());
        granted.GetProperty("reused").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_waiter_whose_session_ends_or_goes_silent_is_dropped_and_the_next_one_moves_up()
    {
        var project = await db.NewProjectAsync("HW");
        var holder = HoldId(await AcquireAsync(project, "gpu:w", user: "owner"));
        var dying = await StartAsync(project);
        var silent = await StartAsync(project, "codex");
        await AcquireAsync(project, "gpu:w", session: dying, wait: 1);
        await AcquireAsync(project, "gpu:w", session: silent, wait: 1);
        (await AcquireAsync(project, "gpu:w", user: "tail", wait: 1)).GetProperty("hold").GetProperty("queue_position").GetInt32().ShouldBe(3);

        await db.CallAsync(WorkTools.EndSession, new { session_id = dying }, "michael");
        (await AcquireAsync(project, "gpu:w", user: "tail", wait: 1)).GetProperty("hold").GetProperty("queue_position").GetInt32().ShouldBe(2);

        // The other waiter's session goes quiet past the threshold, and so does the tail's wait
        // window (it stopped polling): neither keeps a place for callers who are gone. The tail asks
        // again and is first in the queue.
        db.Clock.Advance(PastIdle);
        (await AcquireAsync(project, "gpu:w", user: "tail", wait: 1)).GetProperty("hold").GetProperty("queue_position").GetInt32().ShouldBe(1);
        await ReleaseAsync(holder, "owner");
        var next = await AcquireAsync(project, "gpu:w", user: "tail");
        next.GetProperty("outcome").GetString().ShouldBe("granted");
        next.GetProperty("resource").TryGetProperty("waiters", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_waiting_entry_is_dropped_after_its_wait_window_and_cannot_be_renewed()
    {
        var project = await db.NewProjectAsync("HD");
        var holder = HoldId(await AcquireAsync(project, "gpu:d", user: "owner", ttl: 86_400));
        var waiting = await AcquireAsync(project, "gpu:d", user: "w", wait: 1);
        var waitingId = HoldId(waiting);

        var refused = await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = waitingId }, "w");
        refused.Code.ShouldBe(WorkErrorCodes.Conflict);
        refused.Message.ShouldContain("still waiting");

        db.Clock.Advance(TimeSpan.FromSeconds(WorkHoldLimits.WaiterLingerSeconds + 1));
        await ReleaseAsync(holder, "owner");
        // Nobody waits any more: the slot is free, and the stale waiter did not take it.
        var free = await AcquireAsync(project, "gpu:d", user: "other");
        free.GetProperty("outcome").GetString().ShouldBe("granted");
    }

    [Fact]
    public async Task Releasing_a_waiting_entry_leaves_the_queue()
    {
        var project = await db.NewProjectAsync("HG");
        await AcquireAsync(project, "gpu:g", user: "owner");
        var waitingId = HoldId(await AcquireAsync(project, "gpu:g", user: "w", wait: 1));

        var left = await ReleaseAsync(waitingId, "w");
        left.GetProperty("hold").GetProperty("state").GetString().ShouldBe("cancelled");
        left.GetProperty("resource").TryGetProperty("waiters", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_repeat_by_the_same_holder_and_a_repeated_key_answer_the_same_hold()
    {
        var project = await db.NewProjectAsync("HI");
        var first = await AcquireAsync(project, "gpu:i", user: "alice", key: "run-1");
        var again = await AcquireAsync(project, "gpu:i", user: "alice");
        HoldId(again).ShouldBe(HoldId(first));
        again.GetProperty("reused").GetBoolean().ShouldBeTrue();

        var keyed = await AcquireAsync(project, "gpu:i", user: "alice", key: "run-1");
        HoldId(keyed).ShouldBe(HoldId(first));
        keyed.GetProperty("reused").GetBoolean().ShouldBeTrue();

        // After release the key still answers the hold it made (now released), and takes no new slot.
        await ReleaseAsync(HoldId(first), "alice");
        var replay = await AcquireAsync(project, "gpu:i", user: "alice", key: "run-1");
        HoldId(replay).ShouldBe(HoldId(first));
        replay.GetProperty("hold").GetProperty("state").GetString().ShouldBe("released");
        replay.GetProperty("resource").GetProperty("used").GetInt32().ShouldBe(0);

        // A key belongs to the identity.
        (await AcquireAsync(project, "gpu:i", user: "bob", key: "run-1")).GetProperty("outcome").GetString().ShouldBe("granted");

        var refused = await db.RefusedAsync(WorkTools.Acquire, new { project, resource = "gpu:j", purpose = "p", idempotency_key = "run-1" }, "alice");
        refused.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refused.Message.ShouldContain("gpu:i");
    }

    [Fact]
    public async Task A_resource_that_exists_is_used_as_it_is_and_a_different_capacity_or_labels_is_a_conflict()
    {
        var project = await db.NewProjectAsync("HX");
        await AcquireAsync(project, "gpu:x", user: "alice", capacity: 2, labels: ["a100"]);

        var same = await AcquireAsync(project, "gpu:x", user: "bob", capacity: 2, labels: ["A100"]);
        same.GetProperty("outcome").GetString().ShouldBe("granted");
        same.GetProperty("resource").GetProperty("capacity").GetInt32().ShouldBe(2);

        var capacity = await db.RefusedAsync(WorkTools.Acquire, new { project, resource = "gpu:x", purpose = "p", capacity = 4 });
        capacity.Code.ShouldBe(WorkErrorCodes.Conflict);
        capacity.Message.ShouldContain("capacity 2");
        (await db.RefusedAsync(WorkTools.Acquire, new { project, resource = "gpu:x", purpose = "p", labels = new[] { "h100" } })).Code.ShouldBe(WorkErrorCodes.Conflict);
    }

    [Fact]
    public async Task A_hold_names_its_item_and_the_item_must_belong_to_the_project()
    {
        var project = await db.NewProjectAsync("HM");
        var other = await db.NewProjectAsync("HN");
        var item = (await db.NewItemAsync(project, "Train the model")).GetProperty("key").GetString()!;
        var foreign = (await db.NewItemAsync(other, "Elsewhere")).GetProperty("key").GetString()!;

        (await AcquireAsync(project, "gpu:m", item: item)).GetProperty("hold").GetProperty("item").GetString().ShouldBe(item);
        (await db.RefusedAsync(WorkTools.Acquire, new { project, resource = "gpu:m2", purpose = "p", item = foreign })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // A session works within its project.
        var session = await StartAsync(other);
        var crossed = await db.RefusedAsync(WorkTools.Acquire, new { project, session_id = session, resource = "gpu:m3", purpose = "p" });
        crossed.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        crossed.Message.ShouldContain("session");
    }

    [Fact]
    public async Task Arguments_are_read_strictly_and_a_refused_call_creates_nothing()
    {
        var project = await db.NewProjectAsync("HS");
        var misspelt = await db.RefusedAsync(WorkTools.Acquire, new { project, resource = "gpu:s", purpose = "p", ttl_secs = 5 });
        misspelt.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        misspelt.Message.ShouldContain("ttl_secs");
        misspelt.Message.ShouldContain("ttl_seconds");

        foreach (var arguments in new object[]
        {
            new { project, resource = "gpu:s", purpose = "p", ttl_seconds = "ten" },
            new { project, resource = "gpu:s", purpose = "p", ttl_seconds = 0 },
            new { project, resource = "gpu:s", purpose = "p", ttl_seconds = 86_401 },
            new { project, resource = "gpu:s", purpose = "p", wait_seconds = 56 },
            new { project, resource = "gpu:s", purpose = "p", capacity = 0 },
            new { project, resource = "gpu:s", purpose = "p", priority = "urgent" },
            new { project, resource = "has space", purpose = "p" },
            new { project, resource = "gpu:s", purpose = "   " },
            new { resource = "gpu:s", purpose = "p" },
            new { project, resource = "gpu:s" }
        })
            (await db.RefusedAsync(WorkTools.Acquire, arguments)).Code.ShouldBe(WorkErrorCodes.InvalidArgument, JsonSerializer.Serialize(arguments));

        // The 24 hour ceiling is accepted.
        (await AcquireAsync(project, "gpu:s", ttl: 86_400)).GetProperty("outcome").GetString().ShouldBe("granted");

        // renew_hold takes no session_id (a renewal is not a heartbeat) and no unknown argument.
        var holdId = HoldId(await AcquireAsync(project, "gpu:s2"));
        var withSession = await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = holdId, session_id = Guid.NewGuid().ToString() });
        withSession.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        withSession.Message.ShouldContain("hold_id, ttl_seconds");
        (await db.RefusedAsync(WorkTools.Release, new { hold_id = holdId, force = true })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.RenewHold, new { hold_id = holdId, ttl_seconds = 90_000 })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM work_resources WHERE name IN ('has space', 'gpu:s3')")).ShouldBe(0);
    }

    [Fact]
    public async Task The_briefing_lists_my_holds_and_the_busy_resources_and_names_the_section_it_shortens()
    {
        var project = await db.NewProjectAsync("HK");
        var mine = await StartAsync(project);
        var other = await StartAsync(project, "codex");
        await AcquireAsync(project, "gpu:b1", session: mine, purpose: "my training");
        await AcquireAsync(project, "gpu:b2", session: other, purpose: "their inference");
        await AcquireAsync(project, "gpu:b2", session: mine, wait: 1, purpose: "mine next");
        await AcquireAsync(project, "gpu:idle", user: "x", ttl: 5);
        db.Clock.Advance(TimeSpan.FromSeconds(10));

        var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = mine }, "michael");
        briefing.GetProperty("my_holds").EnumerateArray().Select(hold => (hold.GetProperty("resource").GetString(), hold.GetProperty("state").GetString()))
            .ShouldBe([("gpu:b1", "held"), ("gpu:b2", "waiting")]);
        var busy = briefing.GetProperty("resources").EnumerateArray().ToList();
        busy.Select(resource => resource.GetProperty("name").GetString()).ShouldBe(["gpu:b1", "gpu:b2"]);
        busy[1].GetProperty("holders").EnumerateArray().Single().GetProperty("purpose").GetString().ShouldBe("their inference");
        busy[1].GetProperty("waiters").EnumerateArray().Single().GetProperty("queue_position").GetInt32().ShouldBe(1);

        var tight = await db.CallAsync(WorkTools.GetBriefing, new { session_id = mine, budget_tokens = 200 }, "michael");
        tight.GetProperty("estimated_tokens").GetInt32().ShouldBeLessThanOrEqualTo(200);
    }
}
