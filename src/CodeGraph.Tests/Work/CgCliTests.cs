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

    private static Dictionary<string, string?> HomeEnv(string home, string harness, string? harnessSession = null)
    {
        var environment = Env("http://codegraph.test");
        environment.Remove("CODEGRAPH_SESSION_FILE");
        environment["HOME"] = home;
        environment["CODEGRAPH_HARNESS"] = harness;
        if (harnessSession is not null)
            environment["CODEGRAPH_HARNESS_SESSION"] = harnessSession;
        return environment;
    }

    private static string NewHome() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cg-home-{Guid.NewGuid():N}")).FullName;

    private static StubHandler SessionServer(List<string> requests) => new(request =>
    {
        requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        var id = request.Method == HttpMethod.Post ? Guid.NewGuid().ToString() : request.RequestUri.Segments[^2].TrimEnd('/');
        var briefing = "{\"session_id\":\"" + id + "\",\"budget_tokens\":1500,\"estimated_tokens\":10,\"project\":{\"key\":\"SW\",\"name\":\"SceneWorks\"}}";
        return (HttpStatusCode.OK, request.Method == HttpMethod.Post ? "{\"created\":true,\"session\":{\"id\":\"" + id + "\"},\"briefing\":" + briefing + "}" : briefing);
    });

    [Fact]
    public async Task Two_harnesses_in_one_checkout_get_distinct_sessions_and_each_is_reused_by_its_own_harness()
    {
        var home = NewHome();
        var requests = new List<string>();
        var handler = SessionServer(requests);
        try
        {
            var claude = (await RunAsync(HomeEnv(home, "claude-code"), handler, "brief")).Lines[0];
            var codex = (await RunAsync(HomeEnv(home, "codex"), handler, "brief")).Lines[0];
            claude.ShouldNotBe(codex);
            requests.ShouldBe(["POST /api/work/sessions", "POST /api/work/sessions"]);

            // Each harness finds its own session again.
            (await RunAsync(HomeEnv(home, "claude-code"), handler, "brief")).Lines[0].ShouldBe(claude);
            (await RunAsync(HomeEnv(home, "codex"), handler, "brief")).Lines[0].ShouldBe(codex);
            requests.Count(request => request.StartsWith("GET")).ShouldBe(2);

            // Two sessions of one harness (the harness's own session ids) stay apart too.
            var other = (await RunAsync(HomeEnv(home, "claude-code", "claude-session-2"), handler, "brief")).Lines[0];
            other.ShouldNotBe(claude);
            (await RunAsync(HomeEnv(home, "claude-code", "claude-session-2"), handler, "brief")).Lines[0].ShouldBe(other);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task The_harnesses_own_session_variables_key_the_session_file()
    {
        var home = NewHome();
        var handler = SessionServer([]);
        try
        {
            async Task<string> Brief(string name, string value)
            {
                var environment = HomeEnv(home, "shell");
                environment[name] = value;
                return (await RunAsync(environment, handler, "brief")).Lines[0];
            }

            var a = await Brief("CLAUDE_CODE_SESSION_ID", "one");
            (await Brief("CLAUDE_CODE_SESSION_ID", "two")).ShouldNotBe(a);
            (await Brief("CLAUDE_CODE_SESSION_ID", "one")).ShouldBe(a);
            var codex = await Brief("CODEX_THREAD_ID", "thread-1");
            codex.ShouldNotBe(a);
            (await Brief("CODEX_THREAD_ID", "thread-1")).ShouldBe(codex);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task A_session_file_another_harness_wrote_is_not_reused()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cg-test-{Guid.NewGuid():N}");
        var requests = new List<string>();
        var handler = SessionServer(requests);
        Dictionary<string, string?> For(string harness)
        {
            var environment = Env("http://codegraph.test", file);
            environment["CODEGRAPH_HARNESS"] = harness;
            return environment;
        }

        try
        {
            var claude = (await RunAsync(For("claude-code"), handler, "brief")).Lines[0];
            var codex = (await RunAsync(For("codex"), handler, "brief")).Lines[0];

            codex.ShouldNotBe(claude);
            requests.ShouldBe(["POST /api/work/sessions", "POST /api/work/sessions"]);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".harness");
        }
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_the_session_is_reported_and_the_session_file_kept()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cg-test-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(file, sessionId + "\n");
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return (HttpStatusCode.NotFound, """{"code":"not_found","message":"There is no project, feature or item 'SW-9999'."}""");
        });
        try
        {
            var (exit, lines, _) = await RunAsync(Env("http://codegraph.test", file), handler, "brief", "--item", "SW-9999");

            exit.ShouldBe(0);
            lines.ShouldBe(["codegraph: not_found: There is no project, feature or item 'SW-9999'."]);
            requests.ShouldBe([$"GET /api/work/sessions/{sessionId}/briefing"]);
            (await File.ReadAllTextAsync(file)).Trim().ShouldBe(sessionId);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_argument")]
    public async Task An_unknown_ended_or_foreign_session_starts_a_new_one_and_replaces_the_file(HttpStatusCode status, string code)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cg-test-{Guid.NewGuid():N}");
        var stale = Guid.NewGuid().ToString();
        var fresh = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(file, stale + "\n");
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return request.Method == HttpMethod.Get
                ? (status, $$"""{"code":"{{code}}","message":"Session {{stale}} ended.","reason":"session_unusable"}""")
                : (HttpStatusCode.OK, "{\"created\":true,\"session\":{\"id\":\"" + fresh + "\"},\"briefing\":{\"session_id\":\"" + fresh + "\",\"budget_tokens\":1500,\"estimated_tokens\":1,\"project\":{\"key\":\"SW\",\"name\":\"S\"}}}");
        });
        try
        {
            await RunAsync(Env("http://codegraph.test", file), handler, "brief");

            requests.ShouldBe([$"GET /api/work/sessions/{stale}/briefing", "POST /api/work/sessions"]);
            (await File.ReadAllTextAsync(file)).Trim().ShouldBe(fresh);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".harness");
        }
    }

    [Fact]
    public async Task A_handler_that_ignores_cancellation_is_given_up_on_by_the_watchdog_with_one_line()
    {
        var never = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new DelegateHandler((_, _) => never.Task);
        var environment = Env("http://codegraph.test");
        environment["CODEGRAPH_SESSION_ID"] = Guid.NewGuid().ToString();

        var watch = Stopwatch.StartNew();
        var (exit, lines, _) = await RunAsync(environment, handler, "brief");
        watch.Stop();

        exit.ShouldBe(0);
        lines.Count.ShouldBe(1);
        lines[0].ShouldStartWith("codegraph: unreachable at http://codegraph.test/");
        lines[0].ShouldContain("no answer within 2 s");
        watch.Elapsed.ShouldBeLessThan(CgApp.Deadline);
    }

    [Fact]
    public async Task The_git_probe_child_is_killed_when_the_overall_budget_ends_first()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cg-git-{Guid.NewGuid():N}")).FullName;
        var pidFile = Path.Combine(directory, "pid");
        var git = Path.Combine(directory, "git");
        await File.WriteAllTextAsync(git, $"#!/bin/sh\necho $$ > '{pidFile}'\nexec sleep 30\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var session = Path.Combine(directory, "session");
        await File.WriteAllTextAsync(session, Guid.NewGuid() + "\n");
        // The briefing GET takes 1.1 s and answers that the session is gone: the git probe then
        // starts with less than its own 700 ms left of the overall budget.
        var handler = new DelegateHandler(async (_, _) =>
        {
            await Task.Delay(1100);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":"not_found","message":"gone","reason":"session_unusable"}""", Encoding.UTF8, "application/json")
            };
        });
        var environment = Env("http://codegraph.test", session);
        environment["CODEGRAPH_GIT"] = git;
        try
        {
            var (exit, lines, _) = await RunAsync(environment, handler, "brief");

            exit.ShouldBe(0);
            lines.Count.ShouldBe(1);
            var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
            var alive = true;
            for (var attempt = 0; attempt < 20 && alive; attempt++)
            {
                try
                {
                    alive = !Process.GetProcessById(pid).HasExited;
                }
                catch (ArgumentException)
                {
                    alive = false;
                }

                if (alive)
                    await Task.Delay(100);
            }

            alive.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_unreachable_line_never_echoes_credentials_in_the_url()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var environment = Env($"http://svc-user:hunter2@127.0.0.1:{port}/api?token=abc#frag");
        environment["CODEGRAPH_SESSION_ID"] = Guid.NewGuid().ToString();

        var (_, lines, _) = await RunAsync(environment, null, "brief");

        lines.Count.ShouldBe(1);
        lines[0].ShouldStartWith($"codegraph: unreachable at http://127.0.0.1:{port}/api (");
        foreach (var secret in new[] { "svc-user", "hunter2", "token", "abc", "frag" })
            lines[0].ShouldNotContain(secret);
    }

    [Fact]
    public async Task The_session_directory_and_files_are_private_to_the_owner_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = NewHome();
        try
        {
            await RunAsync(HomeEnv(home, "codex"), SessionServer([]), "brief");

            var directory = Path.Combine(home, ".codegraph", "sessions");
            File.GetUnixFileMode(directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var files = Directory.GetFiles(directory);
            files.Length.ShouldBe(2);
            foreach (var file in files)
                File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
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
