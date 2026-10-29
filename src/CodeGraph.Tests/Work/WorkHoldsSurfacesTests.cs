using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Cg;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The hold tools over REST (E8) and the cg command line (sc-27872): the same strict contract as
/// MCP, a personal access token's entitlements honoured, and `cg hold renew` keeping a hold alive
/// in one line within the deadline while the agent's session is silent.
/// </summary>
public sealed class WorkHoldsSurfacesTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;
    private HttpMessageHandler handler = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service);
        token = await host.CreateTokenAsync("michael");
        handler = host.CreateHandler();
    }

    public async Task DisposeAsync()
    {
        handler.Dispose();
        await host.DisposeAsync();
    }

    private Task<(int Status, JsonElement Body)> Post(string path, object? body = null, string? withToken = null) =>
        host.SendAsync(HttpMethod.Post, path, body is null ? null : JsonSerializer.Serialize(body, WorkJson.Options), withToken ?? token,
            contentType: body is null ? null : "application/json");

    private static async Task<(int Exit, List<string> Lines, string Error)> CgAsync(Dictionary<string, string?> environment, HttpMessageHandler? handler, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CgApp(environment, output, error, handler).RunAsync(args);
        return (exit, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList(), error.ToString());
    }

    private Dictionary<string, string?> CgEnvironment(string? withToken = null) => new()
    {
        ["CODEGRAPH_URL"] = "http://localhost/",
        ["CODEGRAPH_TOKEN"] = withToken ?? token,
        // No session at all: a hold is renewed with the token alone.
        ["CODEGRAPH_SESSION_FILE"] = Path.Combine(Path.GetTempPath(), $"cg-hold-{Guid.NewGuid():N}")
    };

    [Fact]
    public async Task Acquire_renew_and_release_are_reachable_over_rest_with_the_same_strict_arguments()
    {
        var project = await db.NewProjectAsync("RH");
        var (acquireStatus, acquired) = await Post("/api/work/holds", new { project, resource = "gpu:rest", purpose = "rest run", ttl_seconds = 600 });
        acquireStatus.ShouldBe(200);
        acquired.GetProperty("outcome").GetString().ShouldBe("granted");
        var holdId = acquired.GetProperty("hold").GetProperty("id").GetString()!;
        acquired.GetProperty("hold").GetProperty("holder").GetProperty("identity").GetString().ShouldBe("michael");

        var (renewStatus, renewed) = await Post($"/api/work/holds/{holdId}/renew", new { ttl_seconds = 1200 });
        (renewStatus, renewed.GetProperty("ttl_seconds").GetInt32()).ShouldBe((200, 1200));
        (await Post($"/api/work/holds/{holdId}/renew")).Status.ShouldBe(200);

        // Misspelt arguments, an id in the body and a foreign caller are refused, changing nothing.
        var (misspeltStatus, misspelt) = await Post("/api/work/holds", new { project, resource = "gpu:rest", purpose = "x", ttl_secs = 5 });
        (misspeltStatus, misspelt.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
        (await Post($"/api/work/holds/{holdId}/renew", new { hold_id = holdId })).Status.ShouldBe(400);
        (await Post($"/api/work/holds/{holdId}/renew", new { ttl_seconds = 5, session_id = Guid.NewGuid().ToString() })).Status.ShouldBe(400);
        (await Post($"/api/work/holds/{holdId}/release", withToken: await host.CreateTokenAsync("mallory"))).Status.ShouldBe(400);
        (await Post($"/api/work/holds/{Guid.NewGuid()}/release")).Status.ShouldBe(404);

        var (releaseStatus, released) = await Post($"/api/work/holds/{holdId}/release");
        releaseStatus.ShouldBe(200);
        released.GetProperty("hold").GetProperty("state").GetString().ShouldBe("released");
        released.GetProperty("resource").GetProperty("available").GetBoolean().ShouldBeTrue();

        // A renewal after the release is a conflict, not a regrant.
        var (lateStatus, late) = await Post($"/api/work/holds/{holdId}/renew");
        (lateStatus, late.GetProperty("code").GetString()).ShouldBe((409, "conflict"));
    }

    [Fact]
    public async Task A_token_without_the_tool_is_refused_before_anything_is_written()
    {
        var project = await db.NewProjectAsync("RT");
        var onlyAcquire = await host.CreateTokenAsync("michael", "acquire");

        var (acquired, _) = await Post("/api/work/holds", new { project, resource = "gpu:ent", purpose = "p" }, onlyAcquire);
        acquired.ShouldBe(200);
        var holdId = (await Post("/api/work/holds", new { project, resource = "gpu:ent", purpose = "p" }, onlyAcquire)).Body.GetProperty("hold").GetProperty("id").GetString()!;

        var (status, body) = await Post($"/api/work/holds/{holdId}/renew", withToken: onlyAcquire);
        (status, body.GetProperty("code").GetString()).ShouldBe((403, "tool_not_entitled"));
    }

    [Fact]
    public async Task Cg_hold_renew_sends_one_request_with_the_token_alone_and_prints_one_line()
    {
        var holdId = Guid.NewGuid();
        var requests = new List<(string Method, string Path, string? Body, bool Authorized)>();
        var stub = new StubHandler(async request =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(), request.Headers.Authorization is not null));
            return (HttpStatusCode.OK, $$"""{"id":"{{holdId}}","resource":"gpu:4090-box","project":"SW","holder":{"identity":"michael","session_id":"s1","harness":"claude-code"},"purpose":"LoRA run","state":"held","expires_at":"2026-10-29T11:00:00Z","ttl_seconds":600,"priority":"medium","requested_at":"2026-10-29T10:00:00Z"}""");
        });

        var (exit, lines, _) = await CgAsync(CgEnvironment(), stub, "hold", "renew", holdId.ToString(), "--ttl", "600");

        exit.ShouldBe(0);
        requests.ShouldBe([("POST", $"/api/work/holds/{holdId}/renew", """{"ttl_seconds":600}""", true)]);
        lines.ShouldBe([$"codegraph: hold {holdId} renewed: gpu:4090-box held until 2026-10-29T11:00:00Z · michael claude-code · LoRA run"]);

        requests.Clear();
        var (_, plain, _) = await CgAsync(CgEnvironment(), stub, "hold", "renew", holdId.ToString());
        requests.ShouldBe([("POST", $"/api/work/holds/{holdId}/renew", null, true)]);
        plain.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cg_hold_prints_refusals_and_an_unreachable_codegraph_as_one_line_and_exits_zero_and_usage_errors_exit_two()
    {
        var conflict = new StubHandler(_ => Task.FromResult((HttpStatusCode.Conflict, """{"code":"conflict","message":"Hold h on gpu lapsed at 2026-10-29T10:00:00Z. It was not renewed; acquire again.\nNothing was changed."}""")));
        var (exit, lines, _) = await CgAsync(CgEnvironment(), conflict, "hold", "renew", "h");
        exit.ShouldBe(0);
        lines.ShouldBe(["codegraph: conflict: Hold h on gpu lapsed at 2026-10-29T10:00:00Z. It was not renewed; acquire again. Nothing was changed."]);

        var release = new StubHandler(_ => Task.FromResult((HttpStatusCode.OK, """{"hold":{"id":"h","resource":"gpu","project":"SW","holder":{"identity":"michael"},"purpose":"p","state":"released"},"resource":{"name":"gpu"}}""")));
        (await CgAsync(CgEnvironment(), release, "hold", "release", "h")).Lines.ShouldBe(["codegraph: hold h released: gpu released · michael · p"]);

        var never = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var unreachable = await CgAsync(CgEnvironment(), never, "hold", "renew", "h");
        unreachable.Exit.ShouldBe(0);
        unreachable.Lines.Count.ShouldBe(1);
        unreachable.Lines[0].ShouldStartWith("codegraph: unreachable at http://localhost/");

        foreach (var args in new[] { new[] { "hold" }, ["hold", "renew"], ["hold", "bogus", "h"], ["hold", "renew", "h", "--budget", "5"], ["hold", "release", "h", "--ttl", "5"], ["hold", "renew", "h", "--ttl", "0"], ["hold", "renew", "--ttl", "5"] })
            (await CgAsync(CgEnvironment(), conflict, args)).Exit.ShouldBe(2, string.Join(' ', args));
    }

    [Fact]
    public async Task Cg_hold_renew_given_up_on_a_codegraph_that_never_answers_ends_within_the_deadline()
    {
        var hang = new StubHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return (HttpStatusCode.OK, "{}");
        });
        var watch = Stopwatch.StartNew();
        var (exit, lines, _) = await CgAsync(CgEnvironment(), hang, "hold", "renew", "h");
        watch.Stop();

        exit.ShouldBe(0);
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("no answer within 2 s");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Cg_hold_renew_extends_the_expiry_of_a_real_hold_in_real_time_and_does_not_wake_the_silent_session()
    {
        var project = await db.NewProjectAsync("CH");
        var session = (await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code" }, "michael")).GetProperty("session").GetProperty("id").GetString()!;
        var acquired = await db.CallAsync(WorkTools.Acquire, new { session_id = session, resource = "gpu:cg", purpose = "long GPU run", ttl_seconds = 30 }, "michael");
        var hold = acquired.GetProperty("hold");
        var holdId = hold.GetProperty("id").GetString()!;
        var before = hold.GetProperty("expires_at").GetDateTime();

        // The agent goes quiet; cg renews on its own.
        db.Clock.Advance(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        var (exit, lines, _) = await CgAsync(CgEnvironment(), handler, "hold", "renew", holdId, "--ttl", "7200");
        watch.Stop();

        exit.ShouldBe(0);
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("renewed");
        watch.Elapsed.ShouldBeLessThan(CgApp.Budget);

        var after = (await db.CallAsync(WorkTools.RenewHold, new { hold_id = holdId, ttl_seconds = 7200 }, "michael")).GetProperty("expires_at").GetDateTime();
        (after - before).ShouldBeGreaterThan(TimeSpan.FromSeconds(7000));

        // Many minutes later (shiftable clock), with the session silent throughout, cg renews again:
        // the hold survives and reads owner unreachable to anyone who looks.
        for (var round = 0; round < 4; round++)
        {
            db.Clock.Advance(TimeSpan.FromMinutes(10));
            var (_, renewed, _) = await CgAsync(CgEnvironment(), handler, "hold", "renew", holdId, "--ttl", "7200");
            renewed.ShouldHaveSingleItem().ShouldContain("renewed");
        }

        var watcherProject = project;
        var watcher = (await db.CallAsync(WorkTools.StartSession, new { project = watcherProject, harness = "codex" }, "michael")).GetProperty("session").GetProperty("id").GetString()!;
        var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = watcher }, "michael");
        var holder = briefing.GetProperty("resources").EnumerateArray().Single().GetProperty("holders").EnumerateArray().Single();
        holder.GetProperty("owner_unreachable").GetBoolean().ShouldBeTrue();
        (await db.Store.GetSessionAsync(Guid.Parse(session)))!.Live.ShouldBeFalse();

        // cg brief renders it as data.
        var rendered = CgRender.Briefing(JsonNode.Parse(briefing.GetRawText())).ToList();
        rendered.ShouldContain(line => line.StartsWith("resource gpu:cg [1/1]") && !line.Contains("available"));
        rendered.ShouldContain(line => line.Contains("held — owner unreachable since") && line.Contains("long GPU run"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<(HttpStatusCode Status, string Body)>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = await answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
