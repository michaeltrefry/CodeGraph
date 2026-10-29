using System.Text.Json;
using CodeGraph.Data;
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
        await SetTimesAsync(connection, recipient, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1));
        await SetTimesAsync(connection, sender, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1));

        // The window of retention is wider than the message's: a cutoff of now makes both sessions old.
        await Pruner.PruneSessionsAsync(DateTime.UtcNow, 100);
        (await ExistsAsync(connection, recipient)).ShouldBeTrue("a later session may still inherit its unread direct message");
        (await ExistsAsync(connection, sender)).ShouldBeFalse();

        await SetTimesAsync(connection, recipient, DateTime.UtcNow.AddDays(-9), DateTime.UtcNow.AddDays(-9));
        await Pruner.PruneSessionsAsync(DateTime.UtcNow, 100);
        (await ExistsAsync(connection, recipient)).ShouldBeFalse();
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

        result.SessionsDeleted.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var session in sessions)
            (await ExistsAsync(connection, session)).ShouldBeFalse();
    }
}
