using System.Diagnostics;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Durable messages against a real MariaDB (sc-27871): per-recipient states, replay from a cursor
/// after a crash, handover of open requests to the next holder of an item, blocking questions to the
/// human, direct messages inherited by the next session, unread counts, ordering under concurrent
/// posts and the long-poll.
/// </summary>
public sealed class WorkMessagesMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly TimeSpan PastIdle = TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5);

    private async Task<string> StartAsync(string project, string harness, string user = "michael", string? repository = null)
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness, host = "dev-mac", repository }, user);
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ClaimAsync(string project, string session, string user = "michael") =>
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, user)).GetProperty("item");

    private async Task<JsonElement> UpdateAsSessionAsync(string id, string session, object fields, string user = "michael")
    {
        var current = await db.CallAsync(WorkTools.GetWork, new { id });
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["session_id"] = session;
        arguments["expected_version"] = current.GetProperty("version").GetInt64();
        return (await db.CallAsync(WorkTools.UpdateWork, arguments, user)).GetProperty("item");
    }

    private async Task<JsonElement> PostAsync(object arguments, string user = "alice") => await db.CallAsync(WorkTools.Post, arguments, user);

    private async Task<JsonElement> InboxAsync(string session, string? cursor = null, string user = "michael", int? limit = null, bool? includeRead = null) =>
        await db.CallAsync(WorkTools.Inbox, new { session_id = session, cursor, limit, include_read = includeRead }, user);

    private static List<string> Ids(JsonElement inbox) => inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();

    private static string IdOf(JsonElement posted) => posted.GetProperty("message").GetProperty("id").GetString()!;

    private async Task<Guid> ProjectIdAsync(string key) => (await db.Store.ResolveAsync(key))!.Id;

    private async Task<T> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters) ?? default!;
    }

    // ---- Acceptance --------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_posted_to_session_A_about_an_item_appears_in_session_B_s_brief_and_inbox_after_B_recovers_the_item_and_stays_open_until_answered()
    {
        var project = await db.NewProjectAsync("MR");
        var key = (await db.NewItemAsync(project, "Port the store")).GetProperty("key").GetString()!;
        var a = await StartAsync(project, "claude-code");
        await ClaimAsync(project, a);
        await UpdateAsSessionAsync(key, a, new { status = "in_progress" });

        var request = IdOf(await PostAsync(new { project, item = key, to_session = a, kind = "request", body = "Please keep the index names stable.\nThe UI reads them." }));
        // Session A has it in its inbox, delivered.
        var first = await InboxAsync(a);
        Ids(first).ShouldBe([request]);
        first.GetProperty("items")[0].GetProperty("delivery").GetString().ShouldBe("delivered");

        // A dies; B recovers the item.
        db.Clock.Advance(PastIdle);
        var b = await StartAsync(project, "codex", user: "bob");
        var recovered = await db.CallAsync(WorkTools.NextWork, new { project, session_id = b }, "bob");
        recovered.GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        recovered.GetProperty("item").GetProperty("open_messages").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(request);

        // In B's brief: the get_work brief lists it as open, the briefing's inbox section carries it, B's inbox returns it.
        var brief = await db.CallAsync(WorkTools.GetWork, new { id = key, session_id = b }, "bob");
        var open = brief.GetProperty("open_messages").EnumerateArray().Single();
        (open.GetProperty("id").GetString(), open.GetProperty("kind").GetString(), open.GetProperty("state").GetString()).ShouldBe((request, "request", "open"));
        open.GetProperty("summary").GetString().ShouldBe("Please keep the index names stable.");
        var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = b }, "bob");
        briefing.GetProperty("inbox").GetProperty("unread").GetInt32().ShouldBe(1);
        var line = briefing.GetProperty("inbox").GetProperty("items").EnumerateArray().Single();
        (line.GetProperty("id").GetString(), line.GetProperty("handed_over").GetBoolean(), line.GetProperty("item").GetString()).ShouldBe((request, true, key));
        var inbox = await InboxAsync(b, user: "bob");
        Ids(inbox).ShouldBe([request]);
        var delivered = inbox.GetProperty("items")[0];
        delivered.GetProperty("handed_over").GetBoolean().ShouldBeTrue();
        delivered.GetProperty("state").GetString().ShouldBe("open");
        delivered.GetProperty("to_session_id").GetString().ShouldBe(a);
        delivered.GetProperty("body").GetString()!.ShouldContain("The UI reads them.");

        // Reading and acknowledging it does not close it: it stays open until answered.
        await db.CallAsync(WorkTools.Ack, new { session_id = b, messages = new[] { request } }, "bob");
        (await db.CallAsync(WorkTools.GetWork, new { id = key }, "bob")).GetProperty("open_messages").GetArrayLength().ShouldBe(1);

        var answer = await db.CallAsync(WorkTools.Post, new { session_id = b, in_reply_to = request, resolves = "answered", body = "Names kept." }, "bob");
        answer.GetProperty("resolved").GetProperty("state").GetString().ShouldBe("answered");
        answer.GetProperty("resolved").GetProperty("resolved_by").GetProperty("session_id").GetString().ShouldBe(b);
        (await db.CallAsync(WorkTools.GetWork, new { id = key }, "bob")).TryGetProperty("open_messages", out _).ShouldBeFalse();
        // The reply went to the asker, about the same item, with no more arguments than a body.
        answer.GetProperty("message").GetProperty("item").GetString().ShouldBe(key);
        answer.GetProperty("message").GetProperty("to_human").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_that_crashes_after_inbox_delivery_but_before_ack_receives_the_message_again_on_replay()
    {
        var project = await db.NewProjectAsync("RP");
        var session = await StartAsync(project, "claude-code");
        var one = IdOf(await PostAsync(new { project, to_session = session, body = "one" }));
        var two = IdOf(await PostAsync(new { project, to_session = session, body = "two" }));

        var delivered = await InboxAsync(session);
        Ids(delivered).ShouldBe([one, two]);
        delivered.GetProperty("items").EnumerateArray().ShouldAllBe(item => item.GetProperty("delivery").GetString() == "delivered");

        // The session crashed before ack and starts over without a cursor: both are replayed, still unread.
        var replay = await InboxAsync(session);
        Ids(replay).ShouldBe([one, two]);
        // A live loop that passes its cursor back is not given them twice.
        Ids(await InboxAsync(session, delivered.GetProperty("next_cursor").GetString())).ShouldBeEmpty();

        // Ack by id: only that one is gone from the replay.
        var acked = await db.CallAsync(WorkTools.Ack, new { session_id = session, messages = new[] { one } }, "michael");
        acked.GetProperty("acked").GetInt32().ShouldBe(1);
        acked.GetProperty("messages")[0].GetProperty("delivery").GetString().ShouldBe("read");
        Ids(await InboxAsync(session)).ShouldBe([two]);
        // Ack up to a cursor covers the rest; repeating it changes nothing.
        var cursor = replay.GetProperty("next_cursor").GetString();
        (await db.CallAsync(WorkTools.Ack, new { session_id = session, up_to = cursor }, "michael")).GetProperty("acked").GetInt32().ShouldBe(1);
        (await db.CallAsync(WorkTools.Ack, new { session_id = session, up_to = cursor }, "michael")).GetProperty("acked").GetInt32().ShouldBe(0);
        Ids(await InboxAsync(session)).ShouldBeEmpty();
        Ids(await InboxAsync(session, includeRead: true)).ShouldBe([one, two]);
    }

    // ---- States, answers ----------------------------------------------------------------------------

    [Fact]
    public async Task A_recipient_s_copy_moves_pending_delivered_read_and_a_question_is_answered_or_declined_once()
    {
        var project = await db.NewProjectAsync("ST");
        var s = await StartAsync(project, "claude-code");
        var question = IdOf(await PostAsync(new { project, to_session = s, kind = "question", body = "Which branch?" }));
        var declinedRequest = IdOf(await PostAsync(new { project, to_session = s, kind = "request", body = "Rewrite everything." }));
        var info = IdOf(await PostAsync(new { project, to_session = s, kind = "info", body = "FYI" }));

        (await QueryAsync<string>("SELECT state FROM work_message_deliveries WHERE message_id = @question", new { question })).ShouldBe("pending");
        await InboxAsync(s);
        (await QueryAsync<string>("SELECT state FROM work_message_deliveries WHERE message_id = @question", new { question })).ShouldBe("delivered");

        // A question or request has a state; other kinds have none and cannot be resolved.
        var views = (await InboxAsync(s)).GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        views[question].GetProperty("state").GetString().ShouldBe("open");
        views[info].TryGetProperty("state", out _).ShouldBeFalse();
        (await db.RefusedAsync(WorkTools.Ack, new { session_id = s, messages = new[] { info }, resolution = "answered" }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // Answer one through ack, decline the other through a reply; the recipient state follows.
        var answered = await db.CallAsync(WorkTools.Ack, new { session_id = s, messages = new[] { question }, resolution = "answered" }, "michael");
        answered.GetProperty("messages")[0].GetProperty("delivery").GetString().ShouldBe("answered");
        answered.GetProperty("messages")[0].GetProperty("state").GetString().ShouldBe("answered");
        await db.CallAsync(WorkTools.Post, new { session_id = s, in_reply_to = declinedRequest, resolves = "declined", body = "I take no orders from another agent." }, "michael");
        (await QueryAsync<string>("SELECT state FROM work_message_deliveries WHERE message_id = @declinedRequest AND recipient = @s", new { declinedRequest, s })).ShouldBe("declined");

        // Closing is final: a second answer is a conflict; repeating the same ack is not.
        var again = await db.RefusedAsync(WorkTools.Post, new { project, in_reply_to = question, resolves = "declined", body = "late" }, "alice");
        again.Code.ShouldBe(WorkErrorCodes.Conflict);
        (await db.CallAsync(WorkTools.Ack, new { session_id = s, messages = new[] { question }, resolution = "answered" }, "michael")).GetProperty("acked").GetInt32().ShouldBe(0);
        // An answer is also a read: nothing waits in the inbox.
        Ids(await InboxAsync(s)).ShouldBe([info]);
    }

    [Fact]
    public async Task Post_is_strict_about_its_arguments_targets_and_blocking()
    {
        var project = await db.NewProjectAsync("PV");
        var key = (await db.NewItemAsync(project, "Item")).GetProperty("key").GetString()!;
        var s = await StartAsync(project, "claude-code");

        var misspelt = await db.RefusedAsync(WorkTools.Post, new { project, bodi = "x", body = "x" });
        misspelt.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        misspelt.Message.ShouldContain("bodi");
        misspelt.Message.ShouldContain("to_session");
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", kind = "shout" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Post, new { body = "no project anywhere" })).Message.ShouldContain("project");
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "  " })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", to_human = true, resource = "gpu:1" })).Message.ShouldContain("at most one");
        (await db.RefusedAsync(WorkTools.Post, new { project, item = key, body = "x", resource = "gpu:1" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", to_session = Guid.NewGuid().ToString() })).Message.ShouldContain("not a session of this project");
        (await db.RefusedAsync(WorkTools.Post, new { session_id = s, body = "x", to_session = s }, "michael")).Message.ShouldContain("own session");
        // blocking is only a question to the human about an item.
        (await db.RefusedAsync(WorkTools.Post, new { project, item = key, body = "x", kind = "question", blocking = true })).Message.ShouldContain("blocking");
        (await db.RefusedAsync(WorkTools.Post, new { project, item = key, body = "x", kind = "info", to_human = true, blocking = true })).Message.ShouldContain("blocking");
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", kind = "question", to_human = true, blocking = true })).Message.ShouldContain("blocking");
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", resolves = "answered" })).Message.ShouldContain("in_reply_to");
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", in_reply_to = "not-a-guid" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", in_reply_to = Guid.NewGuid().ToString() })).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.Post, new { project, body = "x", resolves = "answered", in_reply_to = Guid.NewGuid().ToString() })).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.Ack, new { session_id = s, messages = new[] { Guid.NewGuid().ToString() } }, "michael")).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.Ack, new { session_id = s }, "michael")).Message.ShouldContain("exactly one");
        (await db.RefusedAsync(WorkTools.Inbox, new { })).Message.ShouldContain("project");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_answers_the_first_message()
    {
        var project = await db.NewProjectAsync("ID");
        var s = await StartAsync(project, "claude-code");
        var first = await PostAsync(new { project, to_session = s, body = "once", idempotency_key = "k1" });
        var second = await PostAsync(new { project, to_session = s, body = "once", idempotency_key = "k1" });
        first.GetProperty("created").GetBoolean().ShouldBeTrue();
        second.GetProperty("created").GetBoolean().ShouldBeFalse();
        IdOf(second).ShouldBe(IdOf(first));
        Ids(await InboxAsync(s)).Count.ShouldBe(1);
    }

    // ---- Channels --------------------------------------------------------------------------------------

    [Fact]
    public async Task Channels_reach_the_right_sessions_and_never_cross_a_project()
    {
        var project = await db.NewProjectAsync("CH");
        var other = await db.NewProjectAsync("OT");
        var feature = (await db.CallAsync(WorkTools.CreateWork, new { project, feature = new { title = "Feature" } })).GetProperty("feature").GetProperty("key").GetString()!;
        var inFeature = (await db.CallAsync(WorkTools.CreateWork, new { project, item = new { title = "In feature", status = "ready", feature } })).GetProperty("item").GetProperty("key").GetString()!;
        await db.NewItemAsync(project, "Outside the feature");

        var holder = await StartAsync(project, "claude-code");
        var bystander = await StartAsync(project, "codex");
        var stranger = await StartAsync(other, "codex");
        (await ClaimAsync(project, holder)).GetProperty("key").GetString().ShouldBe(inFeature);

        // Project channel: every live session of the project but the sender, none of another project.
        var announced = await db.CallAsync(WorkTools.Post, new { session_id = holder, kind = "announcement", body = "Releasing at noon." }, "michael");
        announced.GetProperty("recipients").GetInt32().ShouldBe(1);
        Ids(await InboxAsync(bystander)).ShouldBe([IdOf(announced)]);
        Ids(await InboxAsync(holder)).ShouldBeEmpty();
        Ids(await InboxAsync(stranger)).ShouldBeEmpty();

        // Feature channel: the sessions holding an item of the feature.
        var featureMessage = await db.CallAsync(WorkTools.Post, new { session_id = bystander, feature, body = "Feature news." }, "michael");
        featureMessage.GetProperty("message").GetProperty("channel").GetString().ShouldBe("feature");
        Ids(await InboxAsync(holder)).ShouldBe([IdOf(featureMessage)]);

        // Resource channel: the project's live sessions.
        var resource = await db.CallAsync(WorkTools.Post, new { session_id = bystander, resource = "gpu:4090-box", kind = "info", body = "GPU box going down." }, "michael");
        resource.GetProperty("message").GetProperty("resource").GetString().ShouldBe("gpu:4090-box");
        Ids(await InboxAsync(holder, includeRead: true)).ShouldContain(IdOf(resource));

        // No leakage: another project's session is not addressable, its items are not nameable, and its messages are not ackable.
        (await db.RefusedAsync(WorkTools.Post, new { project, to_session = stranger, body = "x" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var foreignItem = (await db.NewItemAsync(other, "Foreign")).GetProperty("key").GetString()!;
        (await db.RefusedAsync(WorkTools.Post, new { session_id = holder, item = foreignItem, body = "x" }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Inbox, new { session_id = holder, project = other }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var strangerMessage = IdOf(await PostAsync(new { project = other, to_session = stranger, body = "private" }));
        (await db.RefusedAsync(WorkTools.Ack, new { session_id = holder, messages = new[] { strangerMessage } }, "michael")).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.Post, new { session_id = holder, in_reply_to = strangerMessage, body = "x" }, "michael")).Code.ShouldBe(WorkErrorCodes.NotFound);
        Ids(await InboxAsync(stranger)).ShouldBe([strangerMessage]);
    }

    // ---- Handover ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Open_requests_on_an_item_thread_reach_each_next_holder_once_and_a_closed_one_reaches_nobody()
    {
        var project = await db.NewProjectAsync("HO");
        var key = (await db.NewItemAsync(project, "Handed over")).GetProperty("key").GetString()!;
        // Posted while nobody holds the item: stored on the thread, delivered to no one.
        var request = await PostAsync(new { project, item = key, kind = "request", body = "Mind the migration order." });
        request.GetProperty("recipients").GetInt32().ShouldBe(0);
        var answeredQuestion = IdOf(await PostAsync(new { project, item = key, kind = "question", body = "Already settled?" }));
        await db.CallAsync(WorkTools.Post, new { project, in_reply_to = answeredQuestion, resolves = "answered", body = "Yes." }, "alice");
        var note = await PostAsync(new { project, item = key, kind = "info", body = "Context only." });
        note.GetProperty("recipients").GetInt32().ShouldBe(0);

        var first = await StartAsync(project, "claude-code");
        await ClaimAsync(project, first);
        var firstInbox = await InboxAsync(first);
        // Only the open request is handed over: not the answered question, not information.
        Ids(firstInbox).ShouldBe([IdOf(request)]);

        // Claiming again (a retry, or a reclaim) does not hand it over twice.
        await UpdateAsSessionAsync(key, first, new { status = "in_progress" });
        await UpdateAsSessionAsync(key, first, new { release_claim = new { } });
        await UpdateAsSessionAsync(key, first, new { claim = new { } });
        (await InboxAsync(first, includeRead: true)).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The first session asks a question of its own, then its claim lapses: the second holder gets
        // the request and that question (neither is its own).
        var ownQuestion = IdOf(await db.CallAsync(WorkTools.Post, new { session_id = first, item = key, kind = "question", body = "Who owns the schema?" }, "michael"));
        db.Clock.Advance(PastIdle);
        var second = await StartAsync(project, "codex", user: "bob");
        await ClaimAsync(project, second, "bob");
        Ids(await InboxAsync(second, user: "bob")).ShouldBe([IdOf(request), ownQuestion]);

        // The new holder answers the request: the next holder is handed only what is still open.
        await db.CallAsync(WorkTools.Post, new { session_id = second, in_reply_to = IdOf(request), resolves = "answered", body = "Will do." }, "bob");
        db.Clock.Advance(PastIdle);
        var third = await StartAsync(project, "claude-code", user: "carol");
        await ClaimAsync(project, third, "carol");
        Ids(await InboxAsync(third, user: "carol")).ShouldBe([ownQuestion]);
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("open_messages").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_request_posted_while_another_session_claims_the_item_reaches_that_session_exactly_once()
    {
        var project = await db.NewProjectAsync("HR");
        for (var trial = 0; trial < 12; trial++)
        {
            var key = (await db.NewItemAsync(project, $"Race {trial}")).GetProperty("key").GetString()!;
            var session = await StartAsync(project, $"claude-code-{trial}");
            // Only this item is ready, so next_work claims it.
            var posted = Task.Run(() => PostAsync(new { project, item = key, kind = "request", body = $"Race {trial}" }));
            var claimed = Task.Run(() => ClaimAsync(project, session));
            await Task.WhenAll(posted, claimed);
            (await claimed).GetProperty("key").GetString().ShouldBe(key);

            var request = IdOf(await posted);
            var seen = Ids(await InboxAsync(session, includeRead: true));
            seen.Count(id => id == request).ShouldBe(1, $"trial {trial}");
            await db.CallAsync(WorkTools.UpdateWork, new { id = key, session_id = session, expected_version = (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64(), status = "complete" }, "michael");
        }
    }

    // ---- Blocking questions --------------------------------------------------------------------------------

    [Fact]
    public async Task A_blocking_question_to_the_human_blocks_the_item_until_it_is_answered_or_declined()
    {
        var project = await db.NewProjectAsync("BQ");
        var key = (await db.NewItemAsync(project, "Needs a credential")).GetProperty("key").GetString()!;
        var other = (await db.NewItemAsync(project, "Unrelated", priority: "low")).GetProperty("key").GetString()!;
        var s = await StartAsync(project, "claude-code");

        var asked = await db.CallAsync(WorkTools.Post, new { session_id = s, item = key, to_human = true, kind = "question", blocking = true, body = "Which API key may I use for staging?" }, "michael");
        asked.GetProperty("message").GetProperty("blocking").GetBoolean().ShouldBeTrue();
        asked.GetProperty("message").GetProperty("channel").GetString().ShouldBe("human");
        var question = IdOf(asked);

        // Blocked, with a reason distinct from a dependency's; visible in lists; not handed out.
        var blocked = await db.CallAsync(WorkTools.GetWork, new { id = key });
        blocked.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        blocked.GetProperty("blocked_reason").GetString().ShouldBe("awaiting_human");
        blocked.GetProperty("open_messages").EnumerateArray().Single().GetProperty("blocking").GetBoolean().ShouldBeTrue();
        var row = (await db.CallAsync(WorkTools.FindWork, new { project })).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("key").GetString() == key);
        row.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        (await db.CallAsync(WorkTools.FindWork, new { project, ready = true })).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString())
            .ShouldBe([other]);
        (await ClaimAsync(project, await StartAsync(project, "codex", "bob"), "bob")).GetProperty("key").GetString().ShouldBe(other);

        // The human sees it in their inbox (no session), and answers it.
        var humanInbox = await db.CallAsync(WorkTools.Inbox, new { project }, "dana");
        Ids(humanInbox).ShouldBe([question]);
        var answer = await db.CallAsync(WorkTools.Post, new { project, in_reply_to = question, resolves = "answered", body = "Use the staging key in the vault." }, "dana");
        answer.GetProperty("resolved").GetProperty("resolved_by").GetProperty("name").GetString().ShouldBe("dana");
        var unblocked = await db.CallAsync(WorkTools.GetWork, new { id = key });
        unblocked.GetProperty("blocked").GetBoolean().ShouldBeFalse();
        unblocked.TryGetProperty("blocked_reason", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.FindWork, new { project, ready = true })).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString())
            .ShouldContain(key);
        // The asker gets the answer in its inbox without having named a target.
        var askerInbox = await InboxAsync(s);
        askerInbox.GetProperty("items").EnumerateArray().Single().GetProperty("body").GetString()!.ShouldContain("vault");
        // The human's copy is answered; acking by the human is idempotent.
        (await db.CallAsync(WorkTools.Inbox, new { project, include_read = true }, "dana")).GetProperty("items")[0].GetProperty("delivery").GetString().ShouldBe("answered");

        // Declining unblocks as well.
        var key2 = (await db.NewItemAsync(project, "Needs money")).GetProperty("key").GetString()!;
        var question2 = IdOf(await PostAsync(new { project, item = key2, to_human = true, kind = "question", blocking = true, body = "May I buy a license?" }));
        (await db.CallAsync(WorkTools.GetWork, new { id = key2 })).GetProperty("blocked").GetBoolean().ShouldBeTrue();
        await db.CallAsync(WorkTools.Ack, new { project, messages = new[] { question2 }, resolution = "declined" }, "dana");
        (await db.CallAsync(WorkTools.GetWork, new { id = key2 })).GetProperty("blocked").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_dependency_s_reason_comes_before_awaiting_human_and_a_non_blocking_question_blocks_nothing()
    {
        var project = await db.NewProjectAsync("BD");
        var blocker = (await db.NewItemAsync(project, "Blocker")).GetProperty("key").GetString()!;
        var key = (await db.NewItemAsync(project, "Both", blockedBy: [blocker])).GetProperty("key").GetString()!;
        await PostAsync(new { project, item = key, to_human = true, kind = "question", blocking = true, body = "Both?" });
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("blocked_reason").GetString().ShouldBe("waiting");

        var calm = (await db.NewItemAsync(project, "Calm")).GetProperty("key").GetString()!;
        await PostAsync(new { project, item = calm, to_human = true, kind = "question", body = "Whenever you have a minute." });
        (await db.CallAsync(WorkTools.GetWork, new { id = calm })).GetProperty("blocked").GetBoolean().ShouldBeFalse();
    }

    // ---- Direct messages across sessions -------------------------------------------------------------------

    [Fact]
    public async Task A_direct_message_with_no_item_is_inherited_by_the_next_session_of_the_same_project_and_repository_exactly_once()
    {
        var project = await db.NewProjectAsync("DM");
        var ended = await StartAsync(project, "claude-code", repository: "sceneworks");
        var message = IdOf(await PostAsync(new { project, to_session = ended, body = "Remember to run the migration." }));
        var readMessage = IdOf(await PostAsync(new { project, to_session = ended, body = "Already read." }));
        await db.CallAsync(WorkTools.Ack, new { session_id = ended, messages = new[] { readMessage } }, "michael");
        await db.CallAsync(WorkTools.EndSession, new { session_id = ended }, "michael");

        // A session of another repository inherits nothing; the same repository gets the unread one, once.
        var elsewhere = await StartAsync(project, "codex", repository: "inference");
        Ids(await InboxAsync(elsewhere)).ShouldBeEmpty();
        var next = await StartAsync(project, "codex", repository: "sceneworks");
        var briefing = (await db.CallAsync(WorkTools.GetBriefing, new { session_id = next }, "michael")).GetProperty("inbox");
        briefing.GetProperty("unread").GetInt32().ShouldBe(1);
        var inbox = await InboxAsync(next);
        Ids(inbox).ShouldBe([message]);
        inbox.GetProperty("items")[0].GetProperty("handed_over").GetBoolean().ShouldBeTrue();
        var later = await StartAsync(project, "codex", repository: "sceneworks");
        Ids(await InboxAsync(later)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_direct_message_is_no_longer_inherited_seven_days_after_its_session_ended()
    {
        var project = await db.NewProjectAsync("DX");
        var ended = await StartAsync(project, "claude-code", repository: "sceneworks");
        await PostAsync(new { project, to_session = ended, body = "Will expire." });
        await db.CallAsync(WorkTools.EndSession, new { session_id = ended }, "michael");

        db.Clock.Advance(WorkMessageLimits.DirectMessageRetention + TimeSpan.FromHours(1));

        var next = await StartAsync(project, "codex", repository: "sceneworks");
        Ids(await InboxAsync(next)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_that_only_went_silent_counts_as_ended_but_a_live_session_keeps_its_own_messages()
    {
        var project = await db.NewProjectAsync("DL");
        var ended = await StartAsync(project, "claude-code", repository: "sceneworks");
        var crashed = await StartAsync(project, "codex", repository: "sceneworks");
        var question = IdOf(await PostAsync(new { project, to_session = ended, kind = "question", body = "Which port?" }));
        var heads_up = IdOf(await PostAsync(new { project, to_session = crashed, body = "Heads up." }));
        await db.CallAsync(WorkTools.EndSession, new { session_id = ended }, "michael");
        db.Clock.Advance(PastIdle);

        // A live session of another repository (a session of this one would itself be the next session and inherit).
        var live = await StartAsync(project, "claude-code", repository: "inference");
        var liveMessage = IdOf(await PostAsync(new { project, to_session = live, body = "For the live one." }));
        var successor = await StartAsync(project, "claude-code", repository: "sceneworks");

        var inherited = await InboxAsync(successor);
        Ids(inherited).Order().ToList().ShouldBe(new[] { question, heads_up }.Order().ToList());
        inherited.GetProperty("items").EnumerateArray().ShouldAllBe(item => item.GetProperty("handed_over").GetBoolean());
        Ids(await InboxAsync(live)).ShouldBe([liveMessage]);
    }

    // ---- Unread, briefing, human ---------------------------------------------------------------------------

    [Fact]
    public async Task Every_session_response_carries_the_unread_count_and_leaves_it_out_when_nothing_waits()
    {
        var project = await db.NewProjectAsync("UN");
        var key = (await db.NewItemAsync(project, "Counting")).GetProperty("key").GetString()!;
        var s = await StartAsync(project, "claude-code");

        (await db.CallAsync(WorkTools.GetWork, new { id = key, session_id = s }, "michael")).TryGetProperty("unread", out _).ShouldBeFalse();
        await PostAsync(new { project, to_session = s, body = "one" });
        await PostAsync(new { project, to_session = s, body = "two" });

        (await db.CallAsync(WorkTools.GetWork, new { id = key, session_id = s }, "michael")).GetProperty("unread").GetInt32().ShouldBe(2);
        (await db.CallAsync(WorkTools.FindWork, new { project, session_id = s }, "michael")).GetProperty("unread").GetInt32().ShouldBe(2);
        (await db.CallAsync(WorkTools.Changes, new { session_id = s }, "michael")).GetProperty("unread").GetInt32().ShouldBe(2);
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = s }, "michael")).GetProperty("unread").GetInt32().ShouldBe(2);
        var updated = await db.CallAsync(WorkTools.UpdateWork, new { id = key, session_id = s, expected_version = 2, comment = "hi" }, "michael");
        updated.GetProperty("unread").GetInt32().ShouldBe(2);
        updated.GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        // Unrelated calls without a session carry none; the briefing and the inbox carry their own.
        (await db.CallAsync(WorkTools.GetWork, new { id = key }, "michael")).TryGetProperty("unread", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.GetBriefing, new { session_id = s }, "michael")).TryGetProperty("unread", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.GetBriefing, new { session_id = s }, "michael")).GetProperty("inbox").GetProperty("unread").GetInt32().ShouldBe(2);

        var inbox = await InboxAsync(s);
        await db.CallAsync(WorkTools.Ack, new { session_id = s, up_to = inbox.GetProperty("next_cursor").GetString() }, "michael");
        (await db.CallAsync(WorkTools.GetWork, new { id = key, session_id = s }, "michael")).TryGetProperty("unread", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.GetBriefing, new { session_id = s }, "michael")).TryGetProperty("inbox", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_briefing_lists_the_first_unread_as_lines_and_marks_only_those_it_shows_delivered()
    {
        var project = await db.NewProjectAsync("BR");
        var s = await StartAsync(project, "claude-code");
        for (var index = 0; index < 12; index++)
            await PostAsync(new { project, to_session = s, kind = index == 0 ? "request" : "info", body = $"message {index}\nsecond line is never in a summary" });

        var inbox = (await db.CallAsync(WorkTools.GetBriefing, new { session_id = s }, "michael")).GetProperty("inbox");
        inbox.GetProperty("unread").GetInt32().ShouldBe(12);
        var lines = inbox.GetProperty("items").EnumerateArray().ToList();
        lines.Count.ShouldBe(WorkMessageLimits.MaxBriefingInboxLines);
        lines[0].GetProperty("summary").GetString().ShouldBe("message 0");
        (await QueryAsync<int>("SELECT COUNT(*) FROM work_message_deliveries WHERE recipient = @s AND state = 'delivered'", new { s })).ShouldBe(10);
        (await QueryAsync<int>("SELECT COUNT(*) FROM work_message_deliveries WHERE recipient = @s AND state = 'pending'", new { s })).ShouldBe(2);
    }

    [Fact]
    public void The_budget_shortens_the_inbox_lines_but_keeps_its_count()
    {
        var lines = Enumerable.Range(0, 10).Select(index => new MessageLine(Guid.NewGuid(), MessageKind.Info, "alice", new string('x', 150), DateTime.UtcNow)).ToList();
        var briefing = new BriefingView("s", 0, 0, new BriefingProject("SW", "Project"), Inbox: new BriefingInbox(10, lines));

        var fitted = WorkBriefings.Fit(briefing, 300);

        fitted.Truncated!.ShouldContain("inbox");
        fitted.Inbox!.Unread.ShouldBe(10);
        (fitted.Inbox.Items?.Count ?? 0).ShouldBeLessThan(10);
        WorkBriefings.Fit(briefing, 20_000).Inbox!.Items!.Count.ShouldBe(10);
    }

    [Fact]
    public async Task The_humans_have_their_own_inbox_per_project_and_events_are_recorded_in_order()
    {
        var project = await db.NewProjectAsync("HM");
        var s = await StartAsync(project, "claude-code");
        var note = IdOf(await db.CallAsync(WorkTools.Post, new { session_id = s, to_human = true, kind = "info", body = "Done with the schema." }, "michael"));

        var inbox = await db.CallAsync(WorkTools.Inbox, new { project }, "dana");
        Ids(inbox).ShouldBe([note]);
        inbox.GetProperty("items")[0].GetProperty("from").GetProperty("session_id").GetString().ShouldBe(s);
        inbox.GetProperty("items")[0].GetProperty("from").GetProperty("harness").GetString().ShouldBe("claude-code");
        await db.CallAsync(WorkTools.Ack, new { project, messages = new[] { note } }, "dana");
        Ids(await db.CallAsync(WorkTools.Inbox, new { project }, "dana")).ShouldBeEmpty();

        var events = await db.Store.ReadMessageEventsAsync(await ProjectIdAsync(project), 0, 50);
        events.Select(entry => entry.Kind).ShouldBe(["posted", "read"]);
        events[0].Actor.SessionId.ShouldBe(s);
        events[1].Actor.Name.ShouldBe("dana");
        events[1].Recipient.ShouldBe("human");
        events[0].Message!.Summary.ShouldBe("Done with the schema.");
        events.Select(entry => entry.Sequence).ShouldBeInOrder();
        (await db.Store.ReadMessageEventsAsync(await ProjectIdAsync(project), events[0].Sequence, 50)).Count.ShouldBe(1);
    }

    // ---- Ordering and waiting --------------------------------------------------------------------------------

    [Fact]
    public async Task A_reader_following_its_cursor_sees_every_message_of_many_concurrent_posters_exactly_once_and_in_order()
    {
        var project = await db.NewProjectAsync("CC");
        var reader = await StartAsync(project, "claude-code");
        const int posters = 8;
        const int each = 12;

        // A reader that follows its cursor while the posters write.
        var seen = new List<string>();
        var following = Task.Run(async () =>
        {
            string? cursor = null;
            var deadline = Stopwatch.StartNew();
            while (seen.Count < posters * each && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                var page = await InboxAsync(reader, cursor, limit: 5);
                seen.AddRange(Ids(page));
                cursor = page.GetProperty("next_cursor").GetString();
            }
        });

        var written = await Task.WhenAll(Enumerable.Range(0, posters).Select(poster => Task.Run(async () =>
        {
            var ids = new List<string>();
            for (var index = 0; index < each; index++)
                ids.Add(IdOf(await PostAsync(new { project, to_session = reader, body = $"p{poster}-{index}" }, $"poster{poster}")));
            return ids;
        })));
        await following;

        var all = written.SelectMany(ids => ids).ToList();
        seen.Count.ShouldBe(all.Count);
        seen.Distinct().Count().ShouldBe(all.Count);
        seen.OrderBy(id => id).ShouldBe(all.OrderBy(id => id));
        // Each poster's own messages arrive in the order it posted them.
        foreach (var ids in written)
            seen.Where(ids.Contains).ToList().ShouldBe(ids);
        (await QueryAsync<long>("SELECT COUNT(*) FROM work_message_deliveries WHERE recipient = @reader", new { reader })).ShouldBe(all.Count);
    }

    [Fact]
    public async Task The_long_poll_answers_empty_after_its_wait_and_early_when_a_message_arrives()
    {
        var project = await db.NewProjectAsync("LP");
        var s = await StartAsync(project, "claude-code");

        var timer = Stopwatch.StartNew();
        var empty = await db.CallAsync(WorkTools.Inbox, new { session_id = s, wait_seconds = 1 }, "michael");
        timer.Stop();
        Ids(empty).ShouldBeEmpty();
        timer.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        var waiting = Stopwatch.StartNew();
        var poll = db.CallAsync(WorkTools.Inbox, new { session_id = s, wait_seconds = 30 }, "michael");
        await Task.Delay(700);
        var posted = IdOf(await PostAsync(new { project, to_session = s, body = "wake up" }));
        var woken = await poll;
        waiting.Stop();
        Ids(woken).ShouldBe([posted]);
        waiting.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        (await db.RefusedAsync(WorkTools.Inbox, new { session_id = s, wait_seconds = 56 }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Inbox, new { session_id = s, limit = 0 }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Inbox, new { session_id = s, cursor = "nonsense" }, "michael")).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task The_unread_lookup_is_served_by_one_index_over_recipient_project_and_read_state()
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        var columns = (await connection.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'work_message_deliveries' " +
            "AND index_name = 'ix_work_message_deliveries_unread' ORDER BY seq_in_index")).ToList();
        columns.ShouldBe(["recipient", "project_id", "read_at", "sequence"]);
    }
}
