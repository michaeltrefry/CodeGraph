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
    public async Task A_session_named_by_the_change_feed_or_a_handoff_survives_so_its_harness_model_and_host_still_resolve_but_its_closed_claim_rows_go()
    {
        var project = await db.NewProjectAsync("PA");
        var key = (await db.NewItemAsync(project, "Attributed")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, harness: "codex");
        await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "michael");
        await db.CallAsync(WorkTools.EndSession, new { session_id = session, item = key, handoff = new { done = "Did it." } }, "michael");

        await using var connection = await OpenAsync();
        (await ClaimRowsAsync(connection, session, open: false)).ShouldBe(1);
        var old = Cutoff.AddDays(-40);
        await SetTimesAsync(connection, session, old, old);
        var feedBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_changes WHERE actor_session_id = @session", new { session });
        feedBefore.ShouldBeGreaterThan(0);
        var handoffs = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_handoffs WHERE author_session_id = @session", new { session });
        handoffs.ShouldBe(1);

        await Pruner.PruneSessionsAsync(Cutoff, 100);

        (await ExistsAsync(connection, session)).ShouldBeTrue();
        (await ClaimRowsAsync(connection, session)).ShouldBe(0);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_changes WHERE actor_session_id = @session", new { session })).ShouldBe(feedBefore);
        var attributed = await connection.QueryAsync<(string Harness, string Host)>(
            "SELECT s.harness, s.host FROM work_changes c JOIN work_sessions s ON s.id = c.actor_session_id WHERE c.actor_session_id = @session", new { session });
        attributed.ShouldAllBe(row => row.Harness == "codex" && row.Host == "dev-mac");
        var item = await db.CallAsync(WorkTools.GetWork, new { id = key });
        item.GetProperty("handoffs").EnumerateArray().Single().GetProperty("harness").GetString().ShouldBe("codex");
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
