using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Session retention against a real MariaDB (sc-27882): sessions that ended or went idle before the
/// cutoff, and their closed claim rows, are deleted; live sessions, open claims and every session a
/// change-feed entry or handoff still names are not, so attribution keeps resolving.
/// </summary>
public sealed class WorkSessionPruningMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private IWorkSessionPruner Pruner => db.Store;

    private async Task<string> StartAsync(string project, string harness = "claude-code", string? idempotencyKey = null)
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness, model = "opus", host = "dev-mac", idempotency_key = idempotencyKey }, "michael");
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private DateTime Now => db.Clock.GetUtcNow().UtcDateTime;

    private async Task<string> FeedAsync() =>
        JsonSerializer.Serialize(await db.Store.ReadChangesAsync(null, 0, 10_000), WorkJson.Options);

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static Task SetTimesAsync(MySqlConnection connection, string session, DateTime lastSeen, DateTime? ended) =>
        connection.ExecuteAsync("UPDATE work_sessions SET last_seen_at = @lastSeen, ended_at = @ended WHERE id = @session", new { session, lastSeen, ended });

    private static async Task<bool> ExistsAsync(MySqlConnection connection, string session) =>
        await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_sessions WHERE id = @session", new { session }) == 1;

    private static Task<int> ClaimRowsAsync(MySqlConnection connection, string session, bool? open = null) =>
        connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM work_session_claims WHERE session_id = @session" +
            (open is null ? "" : open.Value ? " AND released_at IS NULL" : " AND released_at IS NOT NULL"), new { session });

    private static Task InsertClaimRowAsync(MySqlConnection connection, string session, string itemId, DateTime claimedAt, DateTime? releasedAt) =>
        connection.ExecuteAsync(
            "INSERT INTO work_session_claims (session_id, item_id, claimed_at, released_at) VALUES (@session, @itemId, @claimedAt, @releasedAt)",
            new { session, itemId, claimedAt, releasedAt });

    private async Task<string> ItemIdAsync(string project, string title)
    {
        var item = await db.NewItemAsync(project, title);
        return item.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Sessions_ended_or_idle_beyond_the_cutoff_and_their_closed_claims_are_deleted_and_the_boundary_is_exact()
    {
        var project = await db.NewProjectAsync("PR");
        var itemA = await ItemIdAsync(project, "A");
        var itemB = await ItemIdAsync(project, "B");
        var itemC = await ItemIdAsync(project, "C");
        var old = Cutoff.AddDays(-5);

        var endedOld = await StartAsync(project, idempotencyKey: "prune-key-1");
        var idleOld = await StartAsync(project);
        var atCutoff = await StartAsync(project);
        var justBefore = await StartAsync(project);
        var live = await StartAsync(project);
        await using var connection = await OpenAsync();
        await SetTimesAsync(connection, endedOld, old, old);
        await SetTimesAsync(connection, idleOld, old, null);
        await SetTimesAsync(connection, atCutoff, Cutoff, Cutoff);
        await SetTimesAsync(connection, justBefore, Cutoff.AddTicks(-10), Cutoff.AddTicks(-10));
        await InsertClaimRowAsync(connection, endedOld, itemA, old, old);
        await InsertClaimRowAsync(connection, idleOld, itemB, old, old);
        await InsertClaimRowAsync(connection, atCutoff, itemC, old, old);

        var result = await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, endedOld)).ShouldBeFalse();
        (await ExistsAsync(connection, idleOld)).ShouldBeFalse();
        (await ExistsAsync(connection, justBefore)).ShouldBeFalse();
        (await ExistsAsync(connection, atCutoff)).ShouldBeTrue("a session ended exactly at the cutoff is still inside the window");
        (await ExistsAsync(connection, live)).ShouldBeTrue();
        (await ClaimRowsAsync(connection, endedOld)).ShouldBe(0);
        (await ClaimRowsAsync(connection, idleOld)).ShouldBe(0);
        (await ClaimRowsAsync(connection, atCutoff)).ShouldBe(1);
        result.SessionsDeleted.ShouldBeGreaterThanOrEqualTo(3);
        result.ClaimRowsDeleted.ShouldBeGreaterThanOrEqualTo(2);
        result.IdempotencyKeysDeleted.ShouldBe(1);

        // The start_session key of a deleted session no longer replays onto it: the repeat starts a new session.
        var replay = await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code", idempotency_key = "prune-key-1" }, "michael");
        replay.GetProperty("created").GetBoolean().ShouldBeTrue();
        replay.GetProperty("session").GetProperty("id").GetString().ShouldNotBe(endedOld);
    }

    [Fact]
    public async Task A_session_with_an_open_claim_row_is_kept_with_that_claim_even_when_idle_beyond_the_cutoff()
    {
        var project = await db.NewProjectAsync("PO");
        var item = await ItemIdAsync(project, "Held");
        var session = await StartAsync(project);
        await using var connection = await OpenAsync();
        await SetTimesAsync(connection, session, Cutoff.AddDays(-9), null);
        await InsertClaimRowAsync(connection, session, item, Cutoff.AddDays(-9), null);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, session)).ShouldBeTrue();
        (await ClaimRowsAsync(connection, session, open: true)).ShouldBe(1);
    }

    [Fact]
    public async Task Attribution_on_feed_handoffs_comments_messages_knowledge_attempts_and_holds_is_unchanged_after_its_session_is_pruned()
    {
        var project = await db.NewProjectAsync("PA");
        var key = (await db.NewItemAsync(project, "Attributed")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, harness: "codex");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "michael");
        var current = await db.CallAsync(WorkTools.GetWork, new { id = key });
        await db.CallAsync(WorkTools.UpdateWork, new { id = key, session_id = session, expected_version = current.GetProperty("version").GetInt64(), comment = "Working." }, "michael");
        await db.CallAsync(WorkTools.Post, new { session_id = session, item = key, body = "Status from the session." }, "michael");
        await db.CallAsync(WorkTools.Record, new { session_id = session, entry = new { kind = "fact", summary = "A fact from the session" } }, "michael");
        var hold = (await db.CallAsync(WorkTools.Acquire, new { session_id = session, resource = "gpu:prune", ttl_seconds = 3600, purpose = "run" }, "michael")).GetProperty("hold").GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Release, new { hold_id = hold }, "michael");
        await db.CallAsync(WorkTools.EndSession, new { session_id = session, item = key, handoff = new { done = "Did it." } }, "michael");

        var changesBefore = await FeedAsync();
        var itemBefore = (await db.CallAsync(WorkTools.GetWork, new { id = key, detail = "full" })).GetRawText();
        changesBefore.ShouldContain("codex");
        itemBefore.ShouldContain("codex");

        await using var connection = await OpenAsync();
        (await ClaimRowsAsync(connection, session, open: false)).ShouldBe(1);
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, session, old, old);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, session)).ShouldBeFalse("a referenced session is deleted once its attribution is on the rows");
        (await ClaimRowsAsync(connection, session)).ShouldBe(0);
        (await FeedAsync()).ShouldBe(changesBefore);
        (await db.CallAsync(WorkTools.GetWork, new { id = key, detail = "full" })).GetRawText().ShouldBe(itemBefore);

        // Messages, knowledge, attempts and holds carry the attribution in their own columns.
        async Task<int> Resolved(string table, string sessionColumn, string harness, string model, string host) =>
            await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE {sessionColumn} = @session AND {harness} = 'codex' AND {model} = 'opus' AND {host} = 'dev-mac'", new { session });
        (await Resolved("work_changes", "actor_session_id", "actor_harness", "actor_model", "actor_host")).ShouldBeGreaterThan(0);
        (await Resolved("work_handoffs", "author_session_id", "author_harness", "author_model", "author_host")).ShouldBe(1);
        (await Resolved("work_item_comments", "author_session_id", "author_harness", "author_model", "author_host")).ShouldBe(1);
        (await Resolved("work_messages", "sender_session_id", "sender_harness", "sender_model", "sender_host")).ShouldBe(1);
        (await Resolved("work_message_events", "actor_session_id", "actor_harness", "actor_model", "actor_host")).ShouldBeGreaterThan(0);
        (await Resolved("work_attempts", "session_id", "harness", "model", "host")).ShouldBe(1);
        (await Resolved("work_holds", "session_id", "harness", "model", "host")).ShouldBe(1);
        (await Resolved("knowledge_revisions", "author_session_id", "author_harness", "author_model", "author_host")).ShouldBeGreaterThan(0);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM knowledge_entries WHERE author_session_id = @session AND author_harness = 'codex'", new { session })).ShouldBe(1);
    }

    [Fact]
    public async Task A_session_with_a_live_hold_is_kept_even_when_idle_beyond_the_cutoff()
    {
        var project = await db.NewProjectAsync("PH");
        var session = await StartAsync(project);
        await db.CallAsync(WorkTools.Acquire, new { session_id = session, resource = "gpu:live", ttl_seconds = 3600, purpose = "run" }, "michael");
        await using var connection = await OpenAsync();
        await SetTimesAsync(connection, session, Cutoff.AddDays(-9), null);

        // Real time is far after the cutoff in this test, so the hold is live only if its expiry is ahead of now.
        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, session)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_with_an_unread_direct_message_still_inheritable_is_kept_and_pruned_once_outside_the_inherit_window()
    {
        var project = await db.NewProjectAsync("PM");
        var recipient = await StartAsync(project);
        var sender = await StartAsync(project);
        await db.CallAsync(WorkTools.Post, new { session_id = sender, to_session = recipient, kind = "info", body = "Direct, unread." }, "michael");
        await using var connection = await OpenAsync();
        await SetTimesAsync(connection, recipient, Now.AddDays(-1), Now.AddDays(-1));
        await SetTimesAsync(connection, sender, Now.AddDays(-1), Now.AddDays(-1));

        // The window of retention is wider than the message's: a cutoff of now makes both sessions old.
        await Pruner.PruneSessionsAsync(Now, 100);
        (await ExistsAsync(connection, recipient)).ShouldBeTrue("a later session may still inherit its unread direct message");
        (await ExistsAsync(connection, sender)).ShouldBeFalse();

        await SetTimesAsync(connection, recipient, Now.AddDays(-9), Now.AddDays(-9));
        await Pruner.PruneSessionsAsync(Now, 100);
        (await ExistsAsync(connection, recipient)).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_session_id_column_is_attributed_or_exempt()
    {
        var exempt = new Dictionary<string, string>
        {
            ["work_items.claim_session_id"] = "a live claim; the prune keeps the session while an item names it",
            ["work_session_claims.session_id"] = "the claim rows themselves: open ones keep the session, closed ones are deleted",
            ["work_notices.session_id"] = "undeliverable once the session is gone; deleted with it",
            ["work_messages.to_session_id"] = "a delivery address, not attribution; the recipient is kept while a message is inheritable",
            ["work_message_deliveries.recipient"] = "a delivery address; rows for pruned recipients are left to the message retention of S7a",
            ["knowledge_entries.author_session_id"] = "entries carry author_harness, author_model and author_host from their own write",
            ["lesson_served.session_id"] = "serve bookkeeping (which session a rule was served to), not attribution; kept after the session is pruned so served history stays",
        };
        var attributed = MySqlWorkStore.AttributedColumns.ToDictionary(a => $"{a.Table}.{a.Session}");
        await using var connection = await OpenAsync();
        var columns = (await connection.QueryAsync<(string Table, string Column)>(
            "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = DATABASE() " +
            "AND (column_name = 'session_id' OR column_name LIKE '%\\_session\\_id' OR (table_name = 'work_message_deliveries' AND column_name = 'recipient'))")).ToList();
        columns.ShouldNotBeEmpty();
        foreach (var (table, column) in columns)
        {
            var name = $"{table}.{column}";
            (attributed.ContainsKey(name) || exempt.ContainsKey(name)).ShouldBeTrue($"{name} is neither copied by the prune nor exempt with a reason");
        }

        foreach (var pair in attributed.Values)
        foreach (var attribution in new[] { pair.Harness, pair.Model, pair.Host })
            (await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c",
                new { t = pair.Table, c = attribution })).ShouldBe(1, $"{pair.Table}.{attribution} is missing");
        foreach (var name in exempt.Keys.Concat(attributed.Keys))
            columns.ShouldContain((name.Split('.')[0], name.Split('.')[1]), $"{name} no longer exists; update the lists");
    }

    [Fact]
    public async Task Created_attribution_of_features_and_items_survives_the_prune()
    {
        var project = await db.NewProjectAsync("PC");
        var session = await StartAsync(project, harness: "codex");
        await db.CallAsync(WorkTools.CreateWork, new { project, session_id = session, item = new { title = "Made by a session" } }, "michael");
        await db.CallAsync(WorkTools.CreateWork, new { project, session_id = session, feature = new { title = "Feature by a session" } }, "michael");
        await using var connection = await OpenAsync();
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, session, old, old);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, session)).ShouldBeFalse();
        foreach (var table in new[] { "work_items", "work_features" })
            (await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE created_session_id = @session AND created_harness = 'codex' AND created_model = 'opus' AND created_host = 'dev-mac'", new { session })).ShouldBe(1, table);
    }

    [Fact]
    public async Task Rendered_message_views_the_message_event_feed_and_hold_views_are_unchanged_after_the_session_is_pruned()
    {
        var project = await db.NewProjectAsync("PV");
        var sender = await StartAsync(project, harness: "codex");
        var reader = await StartAsync(project, harness: "claude-code");
        await db.CallAsync(WorkTools.Post, new { session_id = sender, to_session = reader, kind = "info", body = "Direct from the sender." }, "michael");
        await db.CallAsync(WorkTools.Post, new { session_id = sender, kind = "info", body = "To the project." }, "michael");
        var held = await db.CallAsync(WorkTools.Acquire, new { session_id = sender, resource = "gpu:render", ttl_seconds = 3600, purpose = "run", idempotency_key = "render-1" }, "michael");
        await db.CallAsync(WorkTools.Release, new { hold_id = held.GetProperty("hold").GetProperty("id").GetString() }, "michael");
        var projectId = (await db.Store.ResolveAsync(project))!.Id;

        async Task<string> RenderAsync()
        {
            var inbox = (await db.CallAsync(WorkTools.Inbox, new { session_id = reader, include_read = true }, "michael")).GetProperty("items").GetRawText();
            var events = JsonSerializer.Serialize(await db.Store.ReadMessageEventsAsync(projectId, 0, 1000), WorkJson.Options);
            var holds = (await db.CallAsync(WorkTools.Acquire, new { project, resource = "gpu:render", ttl_seconds = 3600, purpose = "run", idempotency_key = "render-1" }, "michael")).GetProperty("hold").GetRawText();
            return inbox + events + holds;
        }

        var before = await RenderAsync();
        before.ShouldContain("codex");
        await using var connection = await OpenAsync();
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, sender, old, old);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, sender)).ShouldBeFalse();
        (await RenderAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task An_item_claim_that_names_a_session_keeps_it_after_the_claim_row_is_closed_and_a_waiting_hold_keeps_its_session()
    {
        var project = await db.NewProjectAsync("PK");
        await db.NewItemAsync(project, "Lapsed");
        var claimant = await StartAsync(project);
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = claimant }, "michael");
        var holder = await StartAsync(project);
        var waiter = await StartAsync(project);
        await db.CallAsync(WorkTools.Acquire, new { session_id = holder, resource = "gpu:wait", ttl_seconds = 3600, purpose = "first" }, "michael");
        var queued = await db.CallAsync(WorkTools.Acquire, new { session_id = waiter, resource = "gpu:wait", ttl_seconds = 60, purpose = "second", wait_seconds = 1 }, "michael");
        queued.GetProperty("hold").GetProperty("state").GetString().ShouldBe("waiting");
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("UPDATE work_session_claims SET released_at = claimed_at WHERE session_id = @claimant", new { claimant });
        await connection.ExecuteAsync("UPDATE work_holds SET wait_expires_at = @later WHERE session_id = @waiter", new { waiter, later = Now.AddHours(1) });
        var old = Cutoff.AddDays(-9);
        foreach (var session in new[] { claimant, waiter })
            await SetTimesAsync(connection, session, old, null);
        await SetTimesAsync(connection, holder, old, old);
        await connection.ExecuteAsync("UPDATE work_holds SET state = 'released', ended_at = @old WHERE session_id = @holder", new { holder, old });

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, claimant)).ShouldBeTrue("the item's claim still names it");
        (await ExistsAsync(connection, waiter)).ShouldBeTrue("a queued waiter is a live hold");
        (await ExistsAsync(connection, holder)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_in_progress_item_whose_claimer_ended_beyond_the_window_is_still_recoverable_and_names_the_previous_holder_after_the_prune()
    {
        var project = await db.NewProjectAsync("PX");
        var key = (await db.NewItemAsync(project, "Recover me")).GetProperty("key").GetString()!;
        var first = await StartAsync(project, harness: "codex");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = first }, "michael");
        var current = await db.CallAsync(WorkTools.GetWork, new { id = key });
        await db.CallAsync(WorkTools.UpdateWork, new { id = key, session_id = first, expected_version = current.GetProperty("version").GetInt64(), status = "in_progress" }, "michael");
        await db.CallAsync(WorkTools.EndSession, new { session_id = first }, "michael");
        await using var connection = await OpenAsync();
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, first, old, old);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, first)).ShouldBeFalse();
        (await ClaimRowsAsync(connection, first)).ShouldBe(0);
        var second = await StartAsync(project, harness: "claude-code");
        var recovered = await db.CallAsync(WorkTools.NextWork, new { project, session_id = second }, "michael");
        recovered.GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        var previous = recovered.GetProperty("recovery").GetProperty("previous_holder");
        previous.GetProperty("session_id").GetString().ShouldBe(first);
        previous.GetProperty("harness").GetString().ShouldBe("codex");
        previous.GetProperty("model").GetString().ShouldBe("opus");
        previous.GetProperty("host").GetString().ShouldBe("dev-mac");
    }

    [Fact]
    public async Task A_heartbeat_for_a_session_that_is_gone_is_refused_as_unusable()
    {
        var project = await db.NewProjectAsync("PT");
        var session = await StartAsync(project);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("DELETE FROM work_sessions WHERE id = @session", new { session });

        var refused = await Should.ThrowAsync<WorkException>(() => db.Store.TouchSessionAsync(Guid.Parse(session), new WorkActor("michael")));

        refused.Reason.ShouldBe(WorkErrorReasons.SessionUnusable);
    }

    [Fact]
    public async Task A_heartbeat_racing_the_prune_either_keeps_the_session_or_is_refused_never_both()
    {
        var project = await db.NewProjectAsync("PZ");
        var sessions = new List<string>();
        for (var i = 0; i < 12; i++)
            sessions.Add(await StartAsync(project));
        await using var connection = await OpenAsync();
        foreach (var session in sessions)
            await SetTimesAsync(connection, session, Cutoff.AddDays(-3), null);

        var touches = sessions.Select(session => Task.Run(async () =>
        {
            try
            {
                await db.Store.TouchSessionAsync(Guid.Parse(session), new WorkActor("michael"));
                return (session, touched: true);
            }
            catch (WorkException exception) when (exception.Reason == WorkErrorReasons.SessionUnusable)
            {
                return (session, touched: false);
            }
        })).ToList();
        var prune = Task.Run(() => Pruner.PruneSessionsAsync(Cutoff, 3));
        var outcomes = await Task.WhenAll(touches);
        await prune;

        foreach (var (session, touched) in outcomes)
            (await ExistsAsync(connection, session)).ShouldBe(touched, $"session {session}: a heartbeat that succeeded must leave the session, one that was refused must find it gone");
    }

    [Fact]
    public async Task Notices_and_start_session_keys_of_a_pruned_session_go_with_it()
    {
        var project = await db.NewProjectAsync("PN");
        var item = await ItemIdAsync(project, "Noticed");
        var session = await StartAsync(project, idempotencyKey: "notice-key");
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO work_notices (id, item_id, session_id, identity, kind, body_json, created_at) VALUES (@id, @item, @session, 'michael', 'changed_by_replan', '{}', @now)",
            new { id = Guid.NewGuid().ToString(), item, session, now = Now });
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, session, old, old);

        var result = await Pruner.PruneSessionsAsync(Cutoff, 100);

        result.IdempotencyKeysDeleted.ShouldBe(1);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_notices WHERE session_id = @session", new { session })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_idempotency_keys WHERE operation = 'session' AND entity_id = @session", new { session })).ShouldBe(0);
    }

    [Fact]
    public async Task Pruning_runs_in_batches_until_nothing_prunable_is_left()
    {
        var project = await db.NewProjectAsync("PB");
        var sessions = new List<string>();
        for (var i = 0; i < 5; i++)
            sessions.Add(await StartAsync(project));
        await using var connection = await OpenAsync();
        foreach (var session in sessions)
            await SetTimesAsync(connection, session, Cutoff.AddDays(-30), null);

        var result = await Pruner.PruneSessionsAsync(Cutoff, 2);

        result.SessionsDeleted.ShouldBe(5, "three transactions: 2, 2 and 1");
        foreach (var session in sessions)
            (await ExistsAsync(connection, session)).ShouldBeFalse();
    }
}
