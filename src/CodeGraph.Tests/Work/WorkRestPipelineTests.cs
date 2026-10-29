using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The REST surface of the work tools through the real pipeline (routing, [Authorize] schemes,
/// the personal access token handler, the hub's tool access predicate), without a database: a
/// token that may not call a tool is refused on the route that runs it, whatever the verb; and a
/// body that cannot be read is invalid_argument in the {code, message} envelope.
/// </summary>
public sealed class WorkRestPipelineTests
{
    private static readonly (HttpMethod Method, string Path, string Tool, string? Body)[] Routes =
    [
        (HttpMethod.Post, "/api/work", WorkTools.CreateWork, """{"new_project":{"key":"SW","name":"n"}}"""),
        (HttpMethod.Post, "/api/work/find", WorkTools.FindWork, """{"project":"SW"}"""),
        (HttpMethod.Post, "/api/work/next", WorkTools.NextWork, """{"project":"SW","marker":"m"}"""),
        (HttpMethod.Get, "/api/work/changes", WorkTools.Changes, null),
        (HttpMethod.Get, "/api/work/SW-1", WorkTools.GetWork, null),
        (HttpMethod.Patch, "/api/work/SW-1", WorkTools.UpdateWork, """{"expected_version":1,"title":"x"}"""),
        (HttpMethod.Post, "/api/work/sessions", WorkTools.StartSession, """{"project":"SW","harness":"codex"}"""),
        (HttpMethod.Post, $"/api/work/sessions/{SessionId}/end", WorkTools.EndSession, "{}"),
        (HttpMethod.Get, $"/api/work/sessions/{SessionId}/briefing", WorkTools.GetBriefing, null),
    ];

    private const string SessionId = "6f1c2c1e-7a55-4c1e-9a43-0c2f4f2e9b11";

    [Fact]
    public async Task A_token_selecting_only_read_tools_reads_but_cannot_write_over_rest()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);
        var token = await host.CreateTokenAsync("agent", WorkTools.GetWork, WorkTools.FindWork);

        foreach (var (method, path, tool, body) in Routes)
        {
            var (status, response) = await host.SendAsync(method, path, body, token);
            if (tool is WorkTools.GetWork or WorkTools.FindWork)
            {
                status.ShouldBe(200, tool);
                work.Calls.ShouldContain(tool);
            }
            else
            {
                status.ShouldBe(403, tool);
                response.GetProperty("code").GetString().ShouldBe("tool_not_entitled", tool);
                response.GetProperty("message").GetString()!.ShouldContain(tool);
                work.Calls.ShouldNotContain(tool);
            }
        }
    }

    [Fact]
    public async Task A_token_entitled_to_a_write_tool_succeeds_and_the_write_is_attributed_to_its_owner()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);
        var token = await host.CreateTokenAsync("agent", WorkTools.UpdateWork);

        var (status, response) = await host.SendAsync(HttpMethod.Patch, "/api/work/SW-1", """{"expected_version":3,"title":"x"}""", token);

        status.ShouldBe(200);
        response.GetProperty("ok").GetBoolean().ShouldBeTrue();
        work.Calls.ShouldBe([WorkTools.UpdateWork]);
        work.LastActor.ShouldBe("agent");
        work.LastArguments.GetProperty("id").GetString().ShouldBe("SW-1");
        work.LastArguments.GetProperty("expected_version").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task A_token_with_every_tool_reaches_every_route()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);
        var token = await host.CreateTokenAsync("agent");

        foreach (var (method, path, _, body) in Routes)
            (await host.SendAsync(method, path, body, token)).Status.ShouldBe(200, path);

        work.Calls.ShouldBe(Routes.Select(route => route.Tool), ignoreOrder: true);
    }

    [Fact]
    public async Task A_tool_the_hub_has_disabled_is_refused_over_rest_even_for_an_entitled_token()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);
        var token = await host.CreateTokenAsync("agent");
        await host.Hub.UpdateToolCatalogStateAsync(WorkTools.UpdateWork, enabled: false, defaultSelected: null, accessClass: null);

        var (status, response) = await host.SendAsync(HttpMethod.Patch, "/api/work/SW-1", """{"expected_version":1,"title":"x"}""", token);

        status.ShouldBe(403);
        response.GetProperty("code").GetString().ShouldBe("tool_disabled");
        (await host.SendAsync(HttpMethod.Get, "/api/work/SW-1", token: token)).Status.ShouldBe(200);
        work.Calls.ShouldBe([WorkTools.GetWork]);
    }

    [Fact]
    public async Task A_disabled_provider_refuses_every_work_route_for_a_token()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);
        var token = await host.CreateTokenAsync("agent");
        await host.Hub.SetProviderEnabledAsync("codegraph", enabled: false, sourceVisible: null);

        foreach (var (method, path, tool, body) in Routes)
        {
            var (status, response) = await host.SendAsync(method, path, body, token);
            status.ShouldBe(403, tool);
            response.GetProperty("code").GetString().ShouldBe("provider_disabled", tool);
        }

        work.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_person_signed_in_on_the_web_is_not_subject_to_token_entitlements_and_no_credentials_is_unauthorized()
    {
        var work = new RecordingWork();
        await using var host = await WorkRestHost.StartAsync(work);

        var (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/work/SW-1", """{"expected_version":1,"title":"x"}""", person: "michael");
        status.ShouldBe(200);
        work.LastActor.ShouldBe("michael");

        (await host.SendAsync(HttpMethod.Get, "/api/work/SW-1")).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Get, "/api/work/SW-1", token: "cgmcp_" + new string('a', 64))).Status.ShouldBe(401);
    }

    [Theory]
    [InlineData("", "application/json", 400, "empty")]
    [InlineData("   ", "application/json", 400, "empty")]
    [InlineData("{", "application/json", 400, "not valid JSON")]
    [InlineData("""{"project": }""", "application/json", 400, "not valid JSON")]
    [InlineData("[]", "application/json", 400, "must be a JSON object")]
    [InlineData("""{"project":"SW","project":"SX"}""", "application/json", 400, "twice")]
    [InlineData("""{"project":"SW","find":{"a":1,"a":2}}""", "application/json", 400, "twice")]
    [InlineData("""{"project":"SW"}""", "text/plain", 415, "application/json")]
    [InlineData("""{"project":"SW"}""", null, 415, "application/json")]
    [InlineData("""{"Project":"SW"}""", "application/json", 400, "Project")]
    public async Task A_body_that_cannot_be_read_is_invalid_argument_in_the_work_envelope(string body, string? contentType, int expectedStatus, string mentions)
    {
        await using var host = await WorkRestHost.StartAsync(new WorkService());
        var token = await host.CreateTokenAsync("agent");

        foreach (var (method, path) in new[] { (HttpMethod.Post, "/api/work/find"), (HttpMethod.Post, "/api/work"), (HttpMethod.Post, "/api/work/next"), (HttpMethod.Patch, "/api/work/SW-1") })
        {
            var (status, response) = await host.SendAsync(method, path, body, token, contentType: contentType);

            status.ShouldBe(expectedStatus, $"{method} {path}");
            response.GetProperty("code").GetString().ShouldBe("invalid_argument", $"{method} {path}");
            response.GetProperty("message").GetString()!.ShouldContain(mentions, Case.Insensitive, $"{method} {path}");
        }
    }

    [Theory]
    [InlineData("""{"project":"SW","status":["ready, in_progress"]}""")]
    [InlineData("""{"project":"SW","status":["InProgress"]}""")]
    [InlineData("""{"project":"SW","status":[2]}""")]
    [InlineData("""{"project":"SW","type":"story, bug"}""")]
    [InlineData("""{"project":"SW","priority":"nonsense"}""")]
    public async Task An_enum_that_is_not_an_exact_wire_name_is_refused_over_rest_listing_the_valid_values(string body)
    {
        await using var host = await WorkRestHost.StartAsync(new WorkService());
        var token = await host.CreateTokenAsync("agent");

        var (status, response) = await host.SendAsync(HttpMethod.Post, "/api/work/find", body, token);

        status.ShouldBe(400);
        response.GetProperty("code").GetString().ShouldBe("invalid_argument");
        response.GetProperty("message").GetString()!.ShouldContain("one of");
    }

    [Fact]
    public async Task Without_the_work_store_rest_answers_a_plain_503_with_no_error_code()
    {
        await using var host = await WorkRestHost.StartAsync(new WorkService());
        var token = await host.CreateTokenAsync("agent");

        var (status, response) = await host.SendAsync(HttpMethod.Get, "/api/work/SW-1", token: token);

        status.ShouldBe(503);
        response.TryGetProperty("code", out _).ShouldBeFalse();
        response.GetProperty("detail").GetString()!.ShouldContain("MariaDB");
    }

    private sealed class RecordingWork : IWorkService
    {
        public List<string> Calls { get; } = [];
        public string? LastActor { get; private set; }
        public JsonElement LastArguments { get; private set; }

        public Task<object> ExecuteAsync(string tool, JsonElement arguments, WorkActor actor, CancellationToken ct = default)
        {
            Calls.Add(tool);
            LastActor = actor.Name;
            LastArguments = arguments.Clone();
            return Task.FromResult<object>(new { ok = true });
        }
    }
}
