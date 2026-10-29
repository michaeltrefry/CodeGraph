using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Retention of messages, deliveries and message events against a real MariaDB (sc-27887): the
/// boundary of what is deleted, and that inbox cursors, heads and AUTO_INCREMENT sequences keep
/// working after a cleanup.
/// </summary>
public sealed class WorkMessageRetentionMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private async Task<string> StartAsync(string project, string repository = "sceneworks")
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code", host = "dev-mac", repository }, "michael");
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private async Task<string> PostAsync(object arguments) =>
        (await db.CallAsync(WorkTools.Post, arguments)).GetProperty("message").GetProperty("id").GetString()!;

    private async Task<List<string>> InboxIdsAsync(string session, string? cursor = null, bool includeRead = false)
    {
        var inbox = await db.CallAsync(WorkTools.Inbox, new { session_id = session, cursor, include_read = includeRead }, "michael");
        return inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();
    }

    private async Task<string> InboxCursorAsync(string session, bool includeRead = true) =>
        (await db.CallAsync(WorkTools.Inbox, new { session_id = session, include_read = includeRead }, "michael")).GetProperty("next_cursor").GetString()!;

    private async Task AckAllAsync(string session, params string[] messages) =>
        await db.CallAsync(WorkTools.Ack, new { session_id = session, messages }, "michael");

    private async Task<T> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters) ?? default!;
    }

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    /// <summary>Makes a message, its deliveries and its events <paramref name="age"/> old.</summary>
    private async Task BackdateAsync(string message, TimeSpan age)
    {
        var at = DateTime.UtcNow - age;
        await ExecuteAsync("UPDATE work_messages SET created_at = @at, resolved_at = IF(resolved_at IS NULL, NULL, @at) WHERE id = @message", new { at, message });
        await ExecuteAsync("UPDATE work_message_deliveries SET created_at = @at, read_at = IF(read_at IS NULL, NULL, @at), delivered_at = IF(delivered_at IS NULL, NULL, @at), resolved_at = IF(resolved_at IS NULL, NULL, @at) WHERE message_id = @message", new { at, message });
        await ExecuteAsync("UPDATE work_message_events SET created_at = @at WHERE message_id = @message", new { at, message });
    }

    private Task<int> MessageCountAsync(string message) => QueryAsync<int>("SELECT COUNT(*) FROM work_messages WHERE id = @message", new { message });

    private Task<int> DeliveryCountAsync(string message) => QueryAsync<int>("SELECT COUNT(*) FROM work_message_deliveries WHERE message_id = @message", new { message });

    private Task<int> EventCountAsync(string message) => QueryAsync<int>("SELECT COUNT(*) FROM work_message_events WHERE message_id = @message", new { message });

    private Task<MessageRetentionResult> PruneAsync(int batchSize = 1000) => db.Store.PruneMessagesAsync(DateTime.UtcNow - Window, batchSize);

    // A message that stays last in the store, so that the newest delivery and event rows are not the ones under test.
    private async Task PostFreshAsync(string project, string session) =>
        await PostAsync(new { project, to_session = session, body = "fresh" });

    [Fact]
    public async Task Read_and_resolved_messages_older_than_the_window_are_deleted_with_their_deliveries_and_events()
    {
        var project = await db.NewProjectAsync("RT");
        var session = await StartAsync(project);
        var oldRead = await PostAsync(new { project, to_session = session, body = "old read" });
        var oldAnswered = await PostAsync(new { project, to_session = session, kind = "request", body = "old request" });
        await AckAllAsync(session, oldRead);
        await db.CallAsync(WorkTools.Ack, new { session_id = session, messages = new[] { oldAnswered }, resolution = "answered" }, "michael");
        var youngRead = await PostAsync(new { project, to_session = session, body = "young read" });
        await AckAllAsync(session, youngRead);
        await PostFreshAsync(project, session);

        await BackdateAsync(oldRead, Window + TimeSpan.FromDays(1));
        await BackdateAsync(oldAnswered, Window + TimeSpan.FromDays(1));
        await BackdateAsync(youngRead, Window - TimeSpan.FromDays(1));

        var result = await PruneAsync();

        result.MessagesDeleted.ShouldBe(2);
        result.DeliveriesDeleted.ShouldBeGreaterThanOrEqualTo(2);
        result.EventsDeleted.ShouldBeGreaterThanOrEqualTo(4);
        foreach (var gone in new[] { oldRead, oldAnswered })
        {
            (await MessageCountAsync(gone)).ShouldBe(0);
            (await DeliveryCountAsync(gone)).ShouldBe(0);
            (await EventCountAsync(gone)).ShouldBe(0);
        }

        (await MessageCountAsync(youngRead)).ShouldBe(1);
        (await DeliveryCountAsync(youngRead)).ShouldBe(1);
        (await EventCountAsync(youngRead)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Open_questions_and_requests_unread_deliveries_and_messages_of_unfinished_items_are_kept_however_old()
    {
        var project = await db.NewProjectAsync("RK");
        var key = (await db.NewItemAsync(project, "Live item")).GetProperty("key").GetString()!;
        var itemId = (await db.Store.ResolveAsync(key))!.Id;
        var session = await StartAsync(project);
        var old = Window + TimeSpan.FromDays(5);

        var openQuestion = await PostAsync(new { project, to_session = session, kind = "question", body = "open?" });
        await AckAllAsync(session, openQuestion); // read, but still open
        var unread = await PostAsync(new { project, to_session = session, body = "unread" });
        var onLiveItem = await PostAsync(new { project, item = key, to_session = session, body = "about the item" });
        await AckAllAsync(session, onLiveItem);
        var onDoneItem = await PostAsync(new { project, item = key, to_session = session, body = "about the item, later" });
        await AckAllAsync(session, onDoneItem);
        await PostFreshAsync(project, session);
        foreach (var message in new[] { openQuestion, unread, onLiveItem, onDoneItem })
            await BackdateAsync(message, old);

        (await PruneAsync()).MessagesDeleted.ShouldBe(0);
        foreach (var message in new[] { openQuestion, unread, onLiveItem, onDoneItem })
            (await MessageCountAsync(message)).ShouldBe(1);

        // The item finishes: its thread is history now, the open question and the unread message still are not.
        await ExecuteAsync("UPDATE work_items SET status = 'complete' WHERE id = @itemId", new { itemId });
        (await PruneAsync()).MessagesDeleted.ShouldBe(2);
        (await MessageCountAsync(onLiveItem)).ShouldBe(0);
        (await MessageCountAsync(onDoneItem)).ShouldBe(0);
        (await MessageCountAsync(openQuestion)).ShouldBe(1);
        (await MessageCountAsync(unread)).ShouldBe(1);
        (await InboxIdsAsync(session)).ShouldContain(unread);
    }

    [Fact]
    public async Task A_replay_after_cleanup_neither_skips_nor_repeats_and_sequences_are_not_reused()
    {
        var project = await db.NewProjectAsync("RC");
        var session = await StartAsync(project);
        var old = Window + TimeSpan.FromDays(3);
        var readOne = await PostAsync(new { project, to_session = session, body = "read one" });
        var readTwo = await PostAsync(new { project, to_session = session, body = "read two" });
        var unread = await PostAsync(new { project, to_session = session, body = "unread" });
        await AckAllAsync(session, readOne, readTwo);
        var cursorBefore = await InboxCursorAsync(session);
        var headBefore = await db.Store.GetInboxHeadAsync();
        var eventHeadBefore = await db.Store.GetMessageEventHeadAsync();
        var sequencesBefore = await QueryAsync<long>("SELECT MAX(sequence) FROM work_messages");
        foreach (var message in new[] { readOne, readTwo, unread })
            await BackdateAsync(message, old);

        var result = await PruneAsync();

        // The newest delivery (the unread message) and event stay, so the heads did not move.
        result.MessagesDeleted.ShouldBe(2);
        (await db.Store.GetInboxHeadAsync()).ShouldBe(headBefore);
        (await db.Store.GetMessageEventHeadAsync()).ShouldBe(eventHeadBefore);
        (await InboxIdsAsync(session)).ShouldBe([unread]);
        (await InboxIdsAsync(session, includeRead: true)).ShouldBe([unread]);
        // A cursor taken before the cleanup replays nothing it saw and loses nothing new.
        (await InboxIdsAsync(session, cursorBefore, includeRead: true)).ShouldBeEmpty();

        var later = await PostAsync(new { project, to_session = session, body = "later" });
        (await InboxIdsAsync(session, cursorBefore, includeRead: true)).ShouldBe([later]);
        (await InboxIdsAsync(session)).ShouldBe([unread, later]);
        (await QueryAsync<long>("SELECT sequence FROM work_messages WHERE id = @later", new { later })).ShouldBeGreaterThan(sequencesBefore);
        (await db.Store.GetInboxHeadAsync()).ShouldBeGreaterThan(headBefore);

        // A second cleanup deletes no more messages (it may delete the one old event the first kept as the head), and the cursor still works.
        var second = await PruneAsync();
        (second.MessagesDeleted, second.DeliveriesDeleted).ShouldBe((0, 0));
        (await InboxIdsAsync(session, cursorBefore, includeRead: true)).ShouldBe([later]);
    }

    [Fact]
    public async Task The_newest_delivery_and_event_are_kept_so_that_heads_do_not_move_back()
    {
        var project = await db.NewProjectAsync("RH");
        var session = await StartAsync(project);
        var only = await PostAsync(new { project, to_session = session, body = "only" });
        await AckAllAsync(session, only);
        await BackdateAsync(only, Window + TimeSpan.FromDays(9));
        var headBefore = await db.Store.GetInboxHeadAsync();
        var eventHeadBefore = await db.Store.GetMessageEventHeadAsync();

        await db.Store.PruneMessagesAsync(DateTime.UtcNow + TimeSpan.FromDays(1), 1000);

        (await db.Store.GetInboxHeadAsync()).ShouldBe(headBefore);
        (await db.Store.GetMessageEventHeadAsync()).ShouldBe(eventHeadBefore);
    }

    [Fact]
    public async Task A_direct_message_a_new_session_can_still_inherit_is_kept_and_inherited_after_cleanup()
    {
        var project = await db.NewProjectAsync("RI", "sceneworks");
        var a = await StartAsync(project);
        var direct = await PostAsync(new { project, to_session = a, body = "for whoever follows" });
        await BackdateAsync(direct, Window + TimeSpan.FromDays(2));
        db.Clock.Advance(TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5));

        await PruneAsync();
        (await MessageCountAsync(direct)).ShouldBe(1);

        var b = await StartAsync(project);
        await db.CallAsync(WorkTools.GetBriefing, new { session_id = b }, "michael");
        (await InboxIdsAsync(b)).ShouldBe([direct]);
    }

    [Fact]
    public async Task Cleanup_deletes_in_batches()
    {
        var project = await db.NewProjectAsync("RB");
        var session = await StartAsync(project);
        var messages = new List<string>();
        for (var i = 0; i < 7; i++)
            messages.Add(await PostAsync(new { project, to_session = session, body = $"m{i}" }));
        await AckAllAsync(session, messages.ToArray());
        await PostFreshAsync(project, session);
        foreach (var message in messages)
            await BackdateAsync(message, Window + TimeSpan.FromDays(4));

        var result = await PruneAsync(batchSize: 2);

        result.MessagesDeleted.ShouldBe(7);
        foreach (var message in messages)
            (await MessageCountAsync(message)).ShouldBe(0);
        (await QueryAsync<int>("SELECT COUNT(*) FROM work_message_events WHERE message_id IN @messages", new { messages })).ShouldBe(0);
    }
}
