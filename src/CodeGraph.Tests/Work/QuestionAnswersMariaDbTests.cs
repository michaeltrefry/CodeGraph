using System.Text.Json;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The answer to an agent's question to the humans reaches the agent (CG-180): the reply is in the
/// asking session's inbox whatever the reply targets, the closed question carries the reply's text
/// as its answer in get_work, a question closed by ack goes back to its asker, and an answer its
/// asker never read passes to the item's next holder.
/// </summary>
public sealed class QuestionAnswersMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly WorkActor Asker = new("claude-asker", WorkActor.AgentKind);
    private static readonly WorkActor Holder = new("claude-holder", WorkActor.AgentKind);
    private static readonly WorkActor Owner = new("michael");

    [Theory]
    [InlineData("answered", "Yes, rebase onto main.")]
    [InlineData("declined", "No, open a new branch instead.")]
    public async Task The_humans_answer_from_the_ui_reaches_the_asking_session_and_stays_on_the_item(string resolution, string answer)
    {
        var project = await db.NewProjectAsync(resolution == "answered" ? "QA" : "QD");
        var item = (await db.NewItemAsync(project, "Rebase the branch")).GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new
        {
            session_id = asker, item, kind = "question", to_human = true, blocking = true, body = "May I rebase this branch?"
        }, Asker)).GetProperty("message").GetProperty("id").GetString()!;

        // The UI's answer names the item as well as in_reply_to; the asker does not hold the item.
        var reply = await db.CallAsAsync(WorkTools.Post, new { project, item, in_reply_to = question, resolves = resolution, body = answer }, Owner);
        var replyId = reply.GetProperty("message").GetProperty("id").GetString()!;
        reply.GetProperty("recipients").GetInt32().ShouldBe(1);

        var inbox = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = asker }, Asker)).GetProperty("items").EnumerateArray().ToList();
        var delivered = inbox.ShouldHaveSingleItem();
        delivered.GetProperty("id").GetString().ShouldBe(replyId);
        delivered.GetProperty("body").GetString().ShouldBe(answer);
        delivered.GetProperty("in_reply_to").GetString().ShouldBe(question);
        delivered.GetProperty("from").GetProperty("name").GetString().ShouldBe(Owner.Name);

        var brief = await db.CallAsAsync(WorkTools.GetWork, new { id = item, session_id = asker }, Asker);
        brief.GetProperty("blocked").GetBoolean().ShouldBeFalse();
        brief.TryGetProperty("open_messages", out _).ShouldBeFalse();
        var closed = brief.GetProperty("questions").EnumerateArray().ShouldHaveSingleItem();
        closed.GetProperty("id").GetString().ShouldBe(question);
        closed.GetProperty("body").GetString().ShouldBe("May I rebase this branch?");
        closed.GetProperty("state").GetString().ShouldBe(resolution);
        closed.GetProperty("answer").GetString().ShouldBe(answer);
        closed.GetProperty("answer_id").GetString().ShouldBe(replyId);
        closed.GetProperty("resolved_by").GetProperty("name").GetString().ShouldBe(Owner.Name);
        closed.TryGetProperty("resolved_at", out var resolvedAt).ShouldBeTrue();
        resolvedAt.GetDateTime().ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-5));

        // The humans' listing keeps the closed question with its answer.
        var projectId = Guid.Parse((await db.CallAsync(WorkTools.GetWork, new { id = project })).GetProperty("id").GetString()!);
        var listed = (await db.Store.ListMessagesAsync(new CodeGraph.Data.MessageListQuery(projectId, null, null, null, null, OpenOnly: false, ToHuman: true, null, 50)))
            .Items.Single(message => message.Id == Guid.Parse(question));
        listed.Answer.ShouldBe(answer);
        listed.AnswerId.ShouldBe(Guid.Parse(replyId));
    }

    [Fact]
    public async Task A_plain_reply_and_the_items_holder_both_get_the_humans_message()
    {
        var project = await db.NewProjectAsync("QH");
        var created = await db.NewItemAsync(project, "Pick a key");
        var item = created.GetProperty("key").GetString()!;
        var holder = await StartAsync(project, Holder);
        await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, expected_version = created.GetProperty("version").GetInt64(), session_id = holder, claim = new { } }, Holder);
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Which key?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;

        var reply = await db.CallAsAsync(WorkTools.Post, new { project, item, in_reply_to = question, body = "Still deciding." }, Owner);
        reply.GetProperty("recipients").GetInt32().ShouldBe(2);
        (await BodiesAsync(asker, Asker)).ShouldBe(["Still deciding."]);
        (await BodiesAsync(holder, Holder)).ShouldBe(["Still deciding."]);

        var open = (await db.CallAsync(WorkTools.GetWork, new { id = item })).GetProperty("questions").EnumerateArray().ShouldHaveSingleItem();
        open.GetProperty("state").GetString().ShouldBe("open");
        open.TryGetProperty("answer", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_question_closed_by_ack_goes_back_to_its_asker()
    {
        var project = await db.NewProjectAsync("QK");
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, kind = "question", to_human = true, body = "Ship tonight?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;

        await db.CallAsAsync(WorkTools.Ack, new { project, messages = new[] { question }, resolution = "declined" }, Owner);

        var back = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = asker }, Asker)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        back.GetProperty("id").GetString().ShouldBe(question);
        back.GetProperty("state").GetString().ShouldBe("declined");
        back.GetProperty("resolved_by").GetProperty("name").GetString().ShouldBe(Owner.Name);
    }

    [Fact]
    public async Task An_answer_its_ended_asker_never_read_passes_to_the_items_next_holder()
    {
        var project = await db.NewProjectAsync("QN");
        var created = await db.NewItemAsync(project, "Choose the region");
        var item = created.GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Which region?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.EndSession, new { session_id = asker }, Asker);

        await db.CallAsAsync(WorkTools.Post, new { project, item, in_reply_to = question, resolves = "answered", body = "Use eu-west." }, Owner);

        var next = await StartAsync(project, Holder);
        var version = (await db.CallAsync(WorkTools.GetWork, new { id = item })).GetProperty("version").GetInt64();
        await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, expected_version = version, session_id = next, claim = new { } }, Holder);
        var handed = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = next }, Holder)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        handed.GetProperty("body").GetString().ShouldBe("Use eu-west.");
        handed.GetProperty("in_reply_to").GetString().ShouldBe(question);
        handed.GetProperty("handed_over").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_answer_to_an_ended_asker_goes_to_the_live_holder_and_passes_on_no_further_once_read()
    {
        var project = await db.NewProjectAsync("QL");
        var item = (await db.NewItemAsync(project, "Choose the size")).GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Which size?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.EndSession, new { session_id = asker }, Asker);
        var holder = await StartAsync(project, Holder);
        await ClaimAsync(item, holder, Holder);

        // A reply with to_human as its only target still reaches the item's live holder.
        var reply = await db.CallAsAsync(WorkTools.Post, new { project, to_human = true, in_reply_to = question, resolves = "answered", body = "Large." }, Owner);
        var replyId = reply.GetProperty("message").GetProperty("id").GetString()!;
        var got = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = holder }, Holder)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        got.GetProperty("body").GetString().ShouldBe("Large.");
        await db.CallAsAsync(WorkTools.Ack, new { session_id = holder, messages = new[] { replyId } }, Holder);
        await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, expected_version = await VersionAsync(item), session_id = holder, release_claim = new { } }, Holder);

        // Read once, it is not handed to the next claimer.
        var next = await StartAsync(project, new WorkActor("claude-next", WorkActor.AgentKind));
        await ClaimAsync(item, next, new WorkActor("claude-next", WorkActor.AgentKind));
        (await db.CallAsAsync(WorkTools.Inbox, new { session_id = next }, new WorkActor("claude-next", WorkActor.AgentKind))).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_question_closed_by_ack_after_its_asker_ended_passes_to_the_next_claimer()
    {
        var project = await db.NewProjectAsync("QE");
        var item = (await db.NewItemAsync(project, "Ship it")).GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Ship tonight?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.EndSession, new { session_id = asker }, Asker);
        await db.CallAsAsync(WorkTools.Ack, new { project, messages = new[] { question }, resolution = "declined" }, Owner);

        var next = await StartAsync(project, Holder);
        await ClaimAsync(item, next, Holder);
        var handed = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = next }, Holder)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        handed.GetProperty("id").GetString().ShouldBe(question);
        handed.GetProperty("state").GetString().ShouldBe("declined");
    }

    [Fact]
    public async Task An_agents_ack_resolution_is_not_a_read_of_the_answer()
    {
        var project = await db.NewProjectAsync("QX");
        var item = (await db.NewItemAsync(project, "Agent decides")).GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var resolver = await StartAsync(project, Holder);
        // A question to the item's thread (not the humans), delivered to its holder, who closes it by ack.
        await ClaimAsync(item, resolver, Holder);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", body = "Merge now?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.EndSession, new { session_id = asker }, Asker);
        await db.CallAsAsync(WorkTools.Ack, new { session_id = resolver, messages = new[] { question }, resolution = "declined" }, Holder);
        await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, expected_version = await VersionAsync(item), session_id = resolver, release_claim = new { } }, Holder);

        var nextActor = new WorkActor("claude-next", WorkActor.AgentKind);
        var next = await StartAsync(project, nextActor);
        await ClaimAsync(item, next, nextActor);
        var handed = (await db.CallAsAsync(WorkTools.Inbox, new { session_id = next }, nextActor)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        handed.GetProperty("id").GetString().ShouldBe(question);
        handed.GetProperty("state").GetString().ShouldBe("declined");
    }

    [Fact]
    public async Task The_migration_links_questions_answered_before_it_and_the_resolved_filter_lists_them()
    {
        var project = await db.NewProjectAsync("QM");
        var item = (await db.NewItemAsync(project, "Old answer")).GetProperty("key").GetString()!;
        var asker = await StartAsync(project, Asker);
        var question = (await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Old question?" }, Asker))
            .GetProperty("message").GetProperty("id").GetString()!;
        await db.CallAsAsync(WorkTools.Post, new { session_id = asker, item, kind = "question", to_human = true, body = "Still open?" }, Asker);
        var replyId = (await db.CallAsAsync(WorkTools.Post, new { project, item, in_reply_to = question, resolves = "answered", body = "Old answer." }, Owner))
            .GetProperty("message").GetProperty("id").GetString()!;

        // The state of a question answered before migration 120.
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE work_messages SET resolution_message_id = NULL WHERE id = @question", new { question });
        var migration = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations/120_question_answers.sql"));
        for (var run = 0; run < 2; run++)
            foreach (var statement in MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(migration)))
                await connection.ExecuteAsync(statement);
        (await connection.QuerySingleAsync<Guid?>("SELECT resolution_message_id FROM work_messages WHERE id = @question", new { question })).ShouldBe(Guid.Parse(replyId));

        var projectId = Guid.Parse((await db.CallAsync(WorkTools.GetWork, new { id = project })).GetProperty("id").GetString()!);
        var resolved = await db.Store.ListMessagesAsync(new CodeGraph.Data.MessageListQuery(projectId, null, null, null, null, OpenOnly: false, ToHuman: true, null, 50, Resolved: true));
        resolved.Items.ShouldHaveSingleItem().Answer.ShouldBe("Old answer.");
    }

    [Fact]
    public async Task A_repeated_next_work_still_carries_the_items_questions()
    {
        var project = await db.NewProjectAsync("QW");
        var item = (await db.NewItemAsync(project, "Next work")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, Holder);
        (await db.CallAsAsync(WorkTools.NextWork, new { project, session_id = session }, Holder)).GetProperty("item").GetProperty("key").GetString().ShouldBe(item);
        await db.CallAsAsync(WorkTools.Post, new { session_id = session, item, kind = "question", to_human = true, body = "Which branch?" }, Holder);

        var again = (await db.CallAsAsync(WorkTools.NextWork, new { project, session_id = session }, Holder)).GetProperty("item");
        again.GetProperty("key").GetString().ShouldBe(item);
        again.GetProperty("questions").EnumerateArray().ShouldHaveSingleItem().GetProperty("body").GetString().ShouldBe("Which branch?");
        again.GetProperty("open_messages").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task The_resolved_listing_puts_the_most_recently_closed_first_and_pages()
    {
        var project = await db.NewProjectAsync("QR");
        var asker = await StartAsync(project, Asker);
        var ids = new List<string>();
        foreach (var body in new[] { "Oldest?", "Middle?", "Newest?" })
            ids.Add((await db.CallAsAsync(WorkTools.Post, new { session_id = asker, kind = "question", to_human = true, body }, Asker))
                .GetProperty("message").GetProperty("id").GetString()!);
        // Answered newest first, oldest last: the oldest question was closed most recently.
        foreach (var id in Enumerable.Reverse(ids))
            await db.CallAsAsync(WorkTools.Post, new { project, in_reply_to = id, resolves = "answered", body = "ok" }, Owner);

        var projectId = Guid.Parse((await db.CallAsync(WorkTools.GetWork, new { id = project })).GetProperty("id").GetString()!);
        var first = await db.Store.ListMessagesAsync(new CodeGraph.Data.MessageListQuery(projectId, null, null, null, null, false, true, null, 2, Resolved: true));
        first.Items.Select(message => message.Body).ShouldBe(["Oldest?", "Middle?"]);
        CodeGraph.Data.ResolvedCursor.TryParse(first.NextBefore!, out var resolvedAt, out var sequence).ShouldBeTrue();
        var second = await db.Store.ListMessagesAsync(new CodeGraph.Data.MessageListQuery(projectId, null, null, null, null, false, true, sequence, 2, Resolved: true, BeforeResolvedAt: resolvedAt));
        second.Items.Select(message => message.Body).ShouldBe(["Newest?"]);
        second.NextBefore.ShouldBeNull();
    }

    private async Task ClaimAsync(string item, string session, WorkActor actor) =>
        await db.CallAsAsync(WorkTools.UpdateWork, new { id = item, expected_version = await VersionAsync(item), session_id = session, claim = new { } }, actor);

    private async Task<long> VersionAsync(string item) => (await db.CallAsync(WorkTools.GetWork, new { id = item })).GetProperty("version").GetInt64();

    private async Task<List<string>> BodiesAsync(string session, WorkActor actor) =>
        (await db.CallAsAsync(WorkTools.Inbox, new { session_id = session }, actor)).GetProperty("items").EnumerateArray()
            .Select(message => message.GetProperty("body").GetString()!).ToList();

    private async Task<string> StartAsync(string project, WorkActor actor)
    {
        await db.AgentAsync(actor.Name);
        return (await db.CallAsAsync(WorkTools.StartSession, new { project, harness = "claude-code", model = "opus" }, actor))
            .GetProperty("session").GetProperty("id").GetString()!;
    }
}
