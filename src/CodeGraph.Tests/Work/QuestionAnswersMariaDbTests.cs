using System.Text.Json;
using CodeGraph.Models.Work;
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
