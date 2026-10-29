using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeGraph.Cg;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The cg command line (sc-27866): exactly one line and exit 0 when CodeGraph is unreachable or
/// does not answer within 2 s, exit 2 only for a usage error, data (not directives) in its output.
/// </summary>
public sealed class CgCliTests
{
    private static readonly string[] Directives = ["you should", "you must", "please", "make sure", "remember to", "do not", "always", "never"];

    private static async Task<(int Exit, List<string> Lines, string Error)> RunAsync(Dictionary<string, string?> environment, HttpMessageHandler? handler, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CgApp(environment, output, error, handler).RunAsync(args);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        return (exit, lines, error.ToString());
    }

    private static Dictionary<string, string?> Env(string url, string? sessionFile = null) => new()
    {
        ["CODEGRAPH_URL"] = url,
        ["CODEGRAPH_TOKEN"] = "cgmcp_test",
        ["CODEGRAPH_SESSION_FILE"] = sessionFile ?? Path.Combine(Path.GetTempPath(), $"cg-test-{Guid.NewGuid():N}"),
        ["CODEGRAPH_PROJECT"] = "SW"
    };

    [Theory]
    [InlineData("brief")]
    [InlineData("inbox")]
    [InlineData("end")]
    public async Task An_unreachable_codegraph_prints_exactly_one_line_and_exits_zero(string command)
    {
        // Nothing listens on the port of a listener that was just closed.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var environment = Env($"http://127.0.0.1:{port}");
        environment["CODEGRAPH_SESSION_ID"] = Guid.NewGuid().ToString();

        var (exit, lines, _) = await RunAsync(environment, null, command);

        exit.ShouldBe(0);
        lines.Count.ShouldBe(1);
        lines[0].ShouldStartWith("codegraph: unreachable at http://127.0.0.1:");
    }

    [Fact]
    public async Task A_codegraph_that_accepts_but_never_answers_is_given_up_on_within_the_deadline_with_one_line()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (true)
                accepted.Add(await listener.AcceptTcpClientAsync());
        });
        try
        {
            var watch = Stopwatch.StartNew();
            var (exit, lines, _) = await RunAsync(Env($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), null, "brief");
            watch.Stop();

            exit.ShouldBe(0);
            lines.ShouldBe([lines[0]]);
            lines[0].ShouldContain("no answer within 2 s");
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        }
        finally
        {
            listener.Stop();
            foreach (var client in accepted)
                client.Dispose();
        }
    }

    [Fact]
    public async Task Usage_errors_alone_exit_non_zero_and_a_missing_url_is_one_line()
    {
        (await RunAsync(Env("http://localhost"), null, "brief", "--budjet", "5")).Exit.ShouldBe(2);
        (await RunAsync(Env("http://localhost"), null, "status")).Exit.ShouldBe(2);
        (await RunAsync(Env("http://localhost"), null)).Exit.ShouldBe(2);

        var (exit, lines, _) = await RunAsync(new Dictionary<string, string?>(), null, "brief");
        exit.ShouldBe(0);
        lines.ShouldBe(["codegraph: not configured (CODEGRAPH_URL is not set to an absolute URL)"]);
    }

    [Fact]
    public async Task An_api_refusal_is_one_line_with_its_code()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Forbidden, """{"code":"tool_not_entitled","message":"This MCP token is not entitled to call 'start_session'."}"""));

        var (exit, lines, _) = await RunAsync(Env("http://codegraph.test"), handler, "brief");

        exit.ShouldBe(0);
        lines.ShouldBe(["codegraph: tool_not_entitled: This MCP token is not entitled to call 'start_session'."]);
    }

    [Fact]
    public async Task Brief_starts_a_session_once_keeps_its_id_and_prints_data_without_empty_sections()
    {
        var sessionFile = Path.Combine(Path.GetTempPath(), $"cg-test-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid().ToString();
        var briefing = $$"""
            {"session_id":"{{sessionId}}","budget_tokens":1500,"estimated_tokens":120,
             "project":{"key":"SW","name":"SceneWorks","goals":["Ship the editor"]},
             "other_sessions":[{"session_id":"s2","identity":"michael","harness":"codex","last_seen_at":"2026-10-29T10:00:00Z","model":"gpt-5","holds":[{"key":"SW-2","title":"Write the briefing","status":"in_progress"}]}],
             "suggested_next":{"key":"SW-3","title":"Recover me","status":"in_progress","recoverable":true,
               "latest_handoff":{"id":"00000000-0000-0000-0000-000000000001","author":"bob","created_at":"2026-10-29T09:00:00Z","next_action":"Write the store." } } }
            """;
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return request.Method == HttpMethod.Post
                ? (HttpStatusCode.OK, $$"""{"created":true,"session":{"id":"{{sessionId}}"},"briefing":{{briefing}}}""")
                : (HttpStatusCode.OK, briefing);
        });

        try
        {
            var first = await RunAsync(Env("http://codegraph.test", sessionFile), handler, "brief");
            var second = await RunAsync(Env("http://codegraph.test", sessionFile), handler, "brief", "--budget", "800");

            requests.ShouldBe(["POST /api/work/sessions", $"GET /api/work/sessions/{sessionId}/briefing?budget_tokens=800"]);
            (await File.ReadAllTextAsync(sessionFile)).Trim().ShouldBe(sessionId);
            first.Exit.ShouldBe(0);
            first.Lines.ShouldBe(second.Lines);
            first.Lines.ShouldBe(
            [
                $"codegraph session {sessionId} · project SW (SceneWorks)",
                "goal: Ship the editor",
                "other session: michael codex gpt-5 · seen 2026-10-29T10:00:00Z · holds SW-2 [in_progress] Write the briefing",
                "suggested next: SW-3 [in_progress] Recover me · recoverable",
                "  last handoff (bob, 2026-10-29T09:00:00Z): next action: Write the store."
            ]);
            foreach (var line in first.Lines)
                foreach (var directive in Directives)
                    line.ShouldNotContain(directive, Case.Insensitive);
        }
        finally
        {
            File.Delete(sessionFile);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>
/// cg against the real REST pipeline and MariaDB: two harnesses brief through cg on one project and
/// each sees the other's live session and claim; cg end records the handoff and releases the claim.
/// </summary>
public sealed class CgCliMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Two_harnesses_brief_through_cg_and_each_sees_the_other_and_cg_end_hands_off()
    {
        await using var host = await WorkRestHost.StartAsync(db.Service);
        var token = await host.CreateTokenAsync("michael");
        var project = await db.NewProjectAsync("CG");
        var first = (await db.NewItemAsync(project, "First item")).GetProperty("key").GetString()!;
        var second = (await db.NewItemAsync(project, "Second item")).GetProperty("key").GetString()!;
        var files = new[] { Path.GetTempFileName(), Path.GetTempFileName() };
        foreach (var file in files)
            File.Delete(file);
        using var handler = host.CreateHandler();

        Dictionary<string, string?> Env(int index, string harness) => new()
        {
            ["CODEGRAPH_URL"] = "http://localhost/",
            ["CODEGRAPH_TOKEN"] = token,
            ["CODEGRAPH_PROJECT"] = project,
            ["CODEGRAPH_HARNESS"] = harness,
            ["CODEGRAPH_SESSION_FILE"] = files[index]
        };

        async Task<List<string>> Cg(int index, string harness, params string[] args)
        {
            var output = new StringWriter();
            (await new CgApp(Env(index, harness), output, TextWriter.Null, handler).RunAsync(args)).ShouldBe(0);
            return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        try
        {
            await Cg(0, "claude-code", "brief");
            await Cg(1, "codex", "brief");
            var sessions = files.Select(file => File.ReadAllText(file).Trim()).ToArray();
            (await db.CallAsync(WorkTools.NextWork, new { project, session_id = sessions[0] }, "michael")).GetProperty("item").GetProperty("key").GetString().ShouldBe(first);
            (await db.CallAsync(WorkTools.NextWork, new { project, session_id = sessions[1] }, "michael")).GetProperty("item").GetProperty("key").GetString().ShouldBe(second);

            var claude = await Cg(0, "claude-code", "brief");
            var codex = await Cg(1, "codex", "brief");

            claude.ShouldContain(line => line.StartsWith("my work: " + first));
            claude.ShouldContain(line => line.StartsWith("other session: michael codex") && line.Contains("holds " + second));
            codex.ShouldContain(line => line.StartsWith("other session: michael claude-code") && line.Contains("holds " + first));
            (await Cg(0, "claude-code", "inbox")).ShouldBeEmpty();

            var ended = await Cg(0, "claude-code", "end", "--done", "Schema half done.", "--next", "Finish the schema.");
            ended.ShouldBe([$"codegraph session {sessions[0]} ended; released {first}; handoff recorded"]);
            File.Exists(files[0]).ShouldBeFalse();
            var item = await db.CallAsync(WorkTools.GetWork, new { id = first });
            item.GetProperty("handoffs").EnumerateArray().Single().GetProperty("next_action").GetString().ShouldBe("Finish the schema.");
            JsonSerializer.Serialize(item).ShouldNotContain("\"claim\"");
        }
        finally
        {
            foreach (var file in files)
                File.Delete(file);
        }
    }
}
