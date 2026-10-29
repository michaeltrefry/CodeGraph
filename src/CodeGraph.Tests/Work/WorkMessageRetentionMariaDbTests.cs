using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private Task<int> ProjectMessagesAsync(string project) =>
        QueryAsync<int>("SELECT COUNT(*) FROM work_messages m JOIN work_projects p ON p.id = m.project_id WHERE p.project_key = @project", new { project });

    private Task<int> ProjectDeliveriesAsync(string project) =>
        QueryAsync<int>("SELECT COUNT(*) FROM work_message_deliveries d JOIN work_projects p ON p.id = d.project_id WHERE p.project_key = @project", new { project });

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

        result.MessagesDeleted.ShouldBeGreaterThanOrEqualTo(2);
        result.DeliveriesDeleted.ShouldBeGreaterThanOrEqualTo(2);
        result.EventsDeleted.ShouldBeGreaterThanOrEqualTo(4);
        (await ProjectMessagesAsync(project)).ShouldBe(2); // the young read one and the fresh one
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

        await PruneAsync();
        foreach (var message in new[] { openQuestion, unread, onLiveItem, onDoneItem })
            (await MessageCountAsync(message)).ShouldBe(1);

        // The item has just been completed: its thread is kept until it has been finished for longer than the window.
        await ExecuteAsync("UPDATE work_items SET status = 'complete', updated_at = @now WHERE id = @itemId", new { itemId, now = DateTime.UtcNow });
        await PruneAsync();
        (await MessageCountAsync(onLiveItem)).ShouldBe(1);
        (await MessageCountAsync(onDoneItem)).ShouldBe(1);

        // Finished for longer than the window, but reopened since (status not finished): kept.
        await ExecuteAsync("UPDATE work_items SET status = 'in_progress', updated_at = @then WHERE id = @itemId", new { itemId, then = DateTime.UtcNow - old });
        await PruneAsync();
        (await MessageCountAsync(onLiveItem)).ShouldBe(1);

        // Finished for longer than the window: the thread is history now; the open question and the unread message still are not.
        await ExecuteAsync("UPDATE work_items SET status = 'complete', updated_at = @then WHERE id = @itemId", new { itemId, then = DateTime.UtcNow - old });
        await PruneAsync();
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
        result.MessagesDeleted.ShouldBeGreaterThanOrEqualTo(2);
        (await MessageCountAsync(readOne)).ShouldBe(0);
        (await MessageCountAsync(readTwo)).ShouldBe(0);
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
        var before = (await ProjectMessagesAsync(project), await ProjectDeliveriesAsync(project));
        await PruneAsync();
        (await ProjectMessagesAsync(project), await ProjectDeliveriesAsync(project)).ShouldBe(before);
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

        result.MessagesDeleted.ShouldBeGreaterThanOrEqualTo(7);
        foreach (var message in messages)
            (await MessageCountAsync(message)).ShouldBe(0);
        (await QueryAsync<int>("SELECT COUNT(*) FROM work_message_events WHERE message_id IN @messages", new { messages })).ShouldBe(0);
    }

    [Fact]
    public async Task An_unread_delivery_to_a_session_dead_beyond_the_inheritance_window_is_prunable_but_one_still_inheritable_is_kept()
    {
        var project = await db.NewProjectAsync("RD");
        var live = await StartAsync(project);
        var longDead = await StartAsync(project);
        var recentlyDead = await StartAsync(project);
        await ExecuteAsync("UPDATE work_sessions SET ended_at = @at WHERE id = @id", new { at = DateTime.UtcNow - TimeSpan.FromDays(40), id = longDead });
        await ExecuteAsync("UPDATE work_sessions SET ended_at = @at WHERE id = @id", new { at = DateTime.UtcNow - TimeSpan.FromDays(3), id = recentlyDead });
        var toLongDead = await PostAsync(new { project, to_session = longDead, body = "nobody will read" });
        var toRecentlyDead = await PostAsync(new { project, to_session = recentlyDead, body = "inheritable" });
        var toLive = await PostAsync(new { project, to_session = live, body = "still unread by a live session" });
        var toHuman = await PostAsync(new { project, to_human = true, body = "for the humans" });
        await PostFreshAsync(project, live);
        foreach (var message in new[] { toLongDead, toRecentlyDead, toLive, toHuman })
            await BackdateAsync(message, Window + TimeSpan.FromDays(5));

        await PruneAsync();

        (await MessageCountAsync(toLongDead)).ShouldBe(0);
        (await DeliveryCountAsync(toLongDead)).ShouldBe(0);
        (await MessageCountAsync(toRecentlyDead)).ShouldBe(1);
        (await MessageCountAsync(toLive)).ShouldBe(1);
        (await MessageCountAsync(toHuman)).ShouldBe(1);
    }

    [Fact]
    public async Task Posts_during_a_cleanup_lose_and_duplicate_no_delivery()
    {
        var project = await db.NewProjectAsync("RP");
        var session = await StartAsync(project);
        var old = new List<string>();
        for (var i = 0; i < 30; i++)
            old.Add(await PostAsync(new { project, to_session = session, body = $"old{i}" }));
        await AckAllAsync(session, old.ToArray());
        foreach (var message in old)
            await BackdateAsync(message, Window + TimeSpan.FromDays(2));

        var posting = Enumerable.Range(0, 20).Select(i => PostAsync(new { project, to_session = session, body = $"new{i}" })).ToArray();
        var pruning = Task.Run(async () =>
        {
            for (var round = 0; round < 3; round++)
                await PruneAsync(batchSize: 3);
        });
        await Task.WhenAll(posting.Append(pruning));

        var posted = posting.Select(task => task.Result).ToList();
        await using var connection = new MySqlConnection(db.ConnectionString);
        var inbox = (await connection.QueryAsync<string>("SELECT CAST(message_id AS CHAR) FROM work_message_deliveries WHERE recipient = @session AND read_at IS NULL", new { session })).ToList();
        inbox.Count.ShouldBe(20);
        inbox.ShouldBe(posted, ignoreOrder: true);
        (await QueryAsync<int>("SELECT COUNT(*) FROM work_message_deliveries WHERE message_id IN @posted", new { posted })).ShouldBe(20);
        foreach (var message in old)
            (await MessageCountAsync(message)).ShouldBe(0);
    }

    [Fact]
    public async Task A_retention_of_zero_days_disables_the_cleanup_and_the_options_bind_through_the_real_registration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeGraph:WorkMessageRetentionOptions:RetentionDays"] = "0",
            ["CodeGraph:WorkMessageRetentionOptions:BatchSize"] = "7"
        }).Build();
        var services = new ServiceCollection();
        services.AddCodeGraphOptions(configuration);
        var bound = services.BuildServiceProvider().GetRequiredService<IOptions<WorkMessageRetentionOptions>>().Value;
        bound.RetentionDays.ShouldBe(0);
        bound.BatchSize.ShouldBe(7);
        new WorkMessageRetentionOptions().RetentionDays.ShouldBe(30);

        var project = await db.NewProjectAsync("RZ");
        var session = await StartAsync(project);
        var old = await PostAsync(new { project, to_session = session, body = "old" });
        await AckAllAsync(session, old);
        await PostFreshAsync(project, session);
        await BackdateAsync(old, TimeSpan.FromDays(400));

        var disabled = new WorkMessageRetentionService(db.Store, Options.Create(bound), NullLogger<WorkMessageRetentionService>.Instance);
        (await disabled.CleanupAsync()).TotalRowsDeleted.ShouldBe(0);
        (await MessageCountAsync(old)).ShouldBe(1);

        var enabled = new WorkMessageRetentionService(db.Store, Options.Create(new WorkMessageRetentionOptions { RetentionDays = 30 }), NullLogger<WorkMessageRetentionService>.Instance);
        await enabled.CleanupAsync();
        (await MessageCountAsync(old)).ShouldBe(0);
    }

    [Fact]
    public async Task The_default_schedule_is_seeded_once_and_a_rerun_of_the_migration_leaves_an_operator_s_schedule_alone()
    {
        (await QueryAsync<int>("SELECT COUNT(*) FROM job_schedules WHERE job_type = 'PruneWorkMessages'")).ShouldBe(1);
        (await QueryAsync<string>("SELECT cron_expression FROM job_schedules WHERE job_type = 'PruneWorkMessages'")).ShouldBe("30 3 * * *");

        await ExecuteAsync("UPDATE job_schedules SET is_enabled = 0, cron_expression = '0 1 * * 0' WHERE job_type = 'PruneWorkMessages'");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations/086_default_work_retention_schedules.sql"));
        await ExecuteAsync(sql);

        (await QueryAsync<int>("SELECT COUNT(*) FROM job_schedules WHERE job_type = 'PruneWorkMessages'")).ShouldBe(1);
        (await QueryAsync<string>("SELECT cron_expression FROM job_schedules WHERE job_type = 'PruneWorkMessages'")).ShouldBe("0 1 * * 0");
        (await QueryAsync<int>("SELECT is_enabled FROM job_schedules WHERE job_type = 'PruneWorkMessages'")).ShouldBe(0);
    }
}
