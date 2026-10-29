using System.Text.Json;
using CodeGraph.Cg;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// post, inbox and ack over the real REST pipeline and MariaDB (E8): the same strict tools, hub-gated
/// for tokens like every work tool; the SSE stream the UI follows; and cg inbox printing real messages.
/// </summary>
public sealed class WorkMessagesRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service, db.Store);
        token = await host.CreateTokenAsync("rest-user");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<(int Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null, string? withToken = null) =>
        host.SendAsync(method, path, body is null ? (method == HttpMethod.Get ? null : "{}") : JsonSerializer.Serialize(body, WorkJson.Options), withToken ?? token);

    [Fact]
    public async Task Post_inbox_and_ack_work_over_rest_with_strict_arguments_and_attribution()
    {
        var project = await db.NewProjectAsync("RM");
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code" }, "rest-user");
        var session = started.GetProperty("session").GetProperty("id").GetString()!;

        var (posted, message) = await Send(HttpMethod.Post, "/api/work/messages", new { project, to_session = session, kind = "question", body = "Which branch?" });
        posted.ShouldBe(200);
        message.GetProperty("created").GetBoolean().ShouldBeTrue();
        message.GetProperty("message").GetProperty("from").GetProperty("name").GetString().ShouldBe("rest-user");
        var id = message.GetProperty("message").GetProperty("id").GetString()!;

        var (refusedStatus, refused) = await Send(HttpMethod.Post, "/api/work/messages", new { project, bodi = "x", body = "x" });
        (refusedStatus, refused.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));

        var (inboxStatus, inbox) = await Send(HttpMethod.Get, $"/api/work/inbox?session_id={session}&limit=5");
        inboxStatus.ShouldBe(200);
        inbox.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(id);
        (await Send(HttpMethod.Get, $"/api/work/inbox?session_id={session}&limt=5")).Status.ShouldBe(400);

        var (ackStatus, acked) = await Send(HttpMethod.Post, "/api/work/ack", new { session_id = session, up_to = inbox.GetProperty("next_cursor").GetString() });
        ackStatus.ShouldBe(200);
        acked.GetProperty("acked").GetInt32().ShouldBe(1);
        (await Send(HttpMethod.Get, $"/api/work/inbox?session_id={session}")).Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await Send(HttpMethod.Get, $"/api/work/inbox")).Body.GetProperty("code").GetString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task A_token_without_the_message_tools_is_denied_them()
    {
        var narrow = await host.CreateTokenAsync("narrow-user", WorkTools.GetWork);
        (await Send(HttpMethod.Post, "/api/work/messages", new { project = "ZZ", body = "x" }, narrow)).Status.ShouldBe(403);
        (await Send(HttpMethod.Post, "/api/work/ack", new { project = "ZZ", up_to = "x" }, narrow)).Status.ShouldBe(403);
        (await Send(HttpMethod.Get, "/api/work/inbox?project=ZZ", null, narrow)).Status.ShouldBe(403);
    }

    [Fact]
    public async Task The_stream_sends_each_message_event_of_a_project_once_in_order_to_a_signed_in_person()
    {
        var project = await db.NewProjectAsync("SE");
        var other = await db.NewProjectAsync("SO");
        var session = (await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex" }, "rest-user")).GetProperty("session").GetProperty("id").GetString()!;
        var before = await db.Store.GetMessageEventHeadAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/work/messages/stream?project={project}&cursor={before}");
        request.Headers.Add("X-Test-User", "dana");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var first = IdOf(await db.CallAsync(WorkTools.Post, new { project, to_session = session, body = "first" }, "rest-user"));
        await db.CallAsync(WorkTools.Post, new { project = other, body = "other project, never streamed" }, "rest-user");
        await db.CallAsync(WorkTools.Ack, new { session_id = session, messages = new[] { first } }, "rest-user");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        var events = new List<(string Id, string Data)>();
        while (events.Count < 2)
        {
            var id = await reader.ReadLineAsync(cancel.Token);
            if (id is null)
                break;
            if (!id.StartsWith("id: "))
                continue;
            (await reader.ReadLineAsync(cancel.Token)).ShouldBe("event: message");
            var data = (await reader.ReadLineAsync(cancel.Token))!;
            events.Add((id[4..], data["data: ".Length..]));
        }

        var parsed = events.Select(entry => JsonDocument.Parse(entry.Data).RootElement).ToList();
        parsed.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(["posted", "read"]);
        parsed.ShouldAllBe(entry => entry.GetProperty("project_key").GetString() == project);
        parsed[0].GetProperty("message").GetProperty("summary").GetString().ShouldBe("first");
        parsed[1].GetProperty("actor").GetProperty("session_id").GetString().ShouldBe(session);
        long.Parse(events[1].Id).ShouldBeGreaterThan(long.Parse(events[0].Id));

        // An unknown project and a caller who is not signed in are refused.
        (await host.SendAsync(HttpMethod.Get, "/api/work/messages/stream?project=NOPE", null, null, "dana")).Status.ShouldBe(404);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/messages/stream?project={project}", null, token)).Status.ShouldBe(401);
    }

    private static string IdOf(JsonElement posted) => posted.GetProperty("message").GetProperty("id").GetString()!;

    [Fact]
    public async Task Cg_inbox_prints_the_messages_waiting_and_with_ack_prints_each_once()
    {
        var project = await db.NewProjectAsync("CI");
        var key = (await db.NewItemAsync(project, "Item")).GetProperty("key").GetString()!;
        var file = Path.GetTempFileName();
        File.Delete(file);
        using var handler = host.CreateHandler();
        var environment = new Dictionary<string, string?>
        {
            ["CODEGRAPH_URL"] = "http://localhost/",
            ["CODEGRAPH_TOKEN"] = token,
            ["CODEGRAPH_PROJECT"] = project,
            ["CODEGRAPH_HARNESS"] = "claude-code",
            ["CODEGRAPH_SESSION_FILE"] = file
        };

        async Task<List<string>> Cg(params string[] args)
        {
            var output = new StringWriter();
            (await new CgApp(environment, output, TextWriter.Null, handler).RunAsync(args)).ShouldBe(0);
            return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        try
        {
            await Cg("brief");
            var session = File.ReadAllText(file).Trim();
            await db.CallAsync(WorkTools.NextWork, new { project, session_id = session }, "rest-user");
            (await Cg("inbox")).ShouldBeEmpty();

            await db.CallAsync(WorkTools.Post, new { project, item = key, to_session = session, kind = "request", body = "Keep the index names stable.\nThe UI reads them." }, "alice");
            await db.CallAsync(WorkTools.Post, new { project, to_session = session, kind = "info", body = "Build is green." }, "alice");

            var brief = await Cg("brief");
            brief.ShouldContain("inbox: 2 unread");
            brief.ShouldContain(line => line.StartsWith("  message ") && line.Contains($"[request, open] from alice about {key}") && line.EndsWith(": Keep the index names stable."));

            var printed = await Cg("inbox", "--ack");
            printed[0].ShouldBe("inbox: 2 messages");
            printed[1].ShouldMatch($@"^message [0-9a-f-]{{36}} \[request, open\] from alice about {key}: Keep the index names stable\. The UI reads them\.$");
            printed[2].ShouldEndWith("[info] from alice: Build is green.");
            // Acknowledged once printed: nothing more, here or in the briefing.
            (await Cg("inbox")).ShouldBeEmpty();
            (await Cg("brief")).ShouldNotContain(line => line.StartsWith("inbox:"));
            // The request is still open on the item thread.
            (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("open_messages").GetArrayLength().ShouldBe(1);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
