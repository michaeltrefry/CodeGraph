using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The work tools over the real MCP streamable-HTTP endpoint (stateful, SSE-framed, as in
/// production), behind the tool-entitlement middleware with the real catalog seeding, on a real
/// MariaDB: a default token sees and calls them, an agent imports a plan and drives an item from
/// ready to complete, every write is in the change feed once and in order, and a misspelt or
/// mistyped argument is refused invalid_argument as an MCP tool error.
/// </summary>
public sealed class WorkMcpEndToEndTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task A_default_token_lists_the_work_tools_and_an_agent_drives_a_plan_over_mcp()
    {
        using var host = await StartHostAsync();
        var agent = await Client.ConnectAsync(host, "claude-agent");

        var tools = await agent.ListToolsAsync();
        foreach (var tool in WorkTools.All)
            tools.ShouldContain(tool.Name);

        var key = "MC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        (await agent.CallAsync("create_work", new { new_project = new { key, name = "Over MCP", repositories = new[] { new { repository = "codegraph", role = "owned" } } } }))
            .Result.GetProperty("project").GetProperty("key").GetString().ShouldBe(key);

        var plan = (await agent.CallAsync("create_work", new
        {
            project = key,
            idempotency_key = "mcp-plan",
            plan = new
            {
                features = new[] { new { @ref = "f", title = "Feature", documents = new[] { new { title = "Doc", body = "Body" } } } },
                items = new object[]
                {
                    new { @ref = "a", title = "First", status = "ready", feature = "f", repository = "codegraph" },
                    new { @ref = "b", title = "Second", status = "ready", feature = "f", blocked_by = new[] { "a" } }
                }
            }
        })).Result;
        plan.GetProperty("items").GetProperty("a").GetProperty("key").GetString().ShouldBe($"{key}-1");

        var next = (await agent.CallAsync("next_work", new { project = key, marker = "session-1", idempotency_key = "n1" })).Result;
        var item = next.GetProperty("item");
        item.GetProperty("key").GetString().ShouldBe($"{key}-1");
        // A retry after a lost response returns the same claim.
        (await agent.CallAsync("next_work", new { project = key, marker = "session-1", idempotency_key = "n1" })).Result
            .GetProperty("item").GetProperty("key").GetString().ShouldBe($"{key}-1");

        var version = item.GetProperty("version").GetInt64();
        foreach (var status in new[] { "in_progress", "in_review", "complete" })
        {
            var updated = (await agent.CallAsync("update_work", new { id = $"{key}-1", expected_version = version, status })).Result.GetProperty("item");
            updated.GetProperty("status").GetString().ShouldBe(status);
            version = updated.GetProperty("version").GetInt64();
        }

        var dependent = (await agent.CallAsync("get_work", new { id = $"{key}-2" })).Result;
        dependent.GetProperty("blocked").GetBoolean().ShouldBeFalse();

        var feed = (await agent.CallAsync("changes", new { project = key, from_start = true })).Result.GetProperty("items").EnumerateArray().ToList();
        feed.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(
            ["project_created", "feature_created", "item_created", "item_created", "claimed", "item_updated", "item_updated", "item_updated"]);
        feed.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("name").GetString() == "claude-agent");
        var sequences = feed.Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        sequences.ShouldBe(sequences.Order().Distinct().ToList());

        var rows = (await agent.CallAsync("find_work", new { project = key })).Result.GetProperty("items").EnumerateArray().ToList();
        rows.Select(row => row.GetProperty("key").GetString()).ShouldBe([$"{key}-1", $"{key}-2"]);
    }

    [Fact]
    public async Task Two_sessions_over_mcp_see_each_other_and_a_session_that_lost_its_claim_gets_a_claim_lost_tool_error()
    {
        using var host = await StartHostAsync();
        var claude = await Client.ConnectAsync(host, "claude-agent");
        var codex = await Client.ConnectAsync(host, "codex-agent");
        var key = "MS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await claude.CallAsync("create_work", new { new_project = new { key, name = "Sessions over MCP" } });
        await claude.CallAsync("create_work", new { project = key, item = new { title = "Only item", status = "ready" } });

        var claudeSession = (await claude.CallAsync("start_session", new { project = key, harness = "claude-code" })).Result.GetProperty("session").GetProperty("id").GetString()!;
        var codexSession = (await codex.CallAsync("start_session", new { project = key, harness = "codex" })).Result.GetProperty("session").GetProperty("id").GetString()!;
        var item = (await claude.CallAsync("next_work", new { project = key, session_id = claudeSession })).Result.GetProperty("item");

        var briefing = (await codex.CallAsync("get_briefing", new { session_id = codexSession })).Result;
        var other = briefing.GetProperty("other_sessions").EnumerateArray().Single();
        other.GetProperty("session_id").GetString().ShouldBe(claudeSession);
        other.GetProperty("holds").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe($"{key}-1");

        // The claude session starts the item and goes silent past the idle threshold; codex recovers
        // it, and claude's next write on it is claim_lost naming the codex session.
        await claude.CallAsync("update_work", new { id = $"{key}-1", expected_version = item.GetProperty("version").GetInt64(), session_id = claudeSession, status = "in_progress" });
        db.Clock.Advance(TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5));
        var recovered = (await codex.CallAsync("next_work", new { project = key, session_id = codexSession })).Result;
        recovered.GetProperty("recovery").GetProperty("previous_holder").GetProperty("session_id").GetString().ShouldBe(claudeSession);

        var lost = await claude.CallAsync("update_work", new
        {
            id = $"{key}-1",
            expected_version = recovered.GetProperty("item").GetProperty("version").GetInt64(),
            session_id = claudeSession,
            comment = "I am back"
        });
        lost.IsError.ShouldBeTrue();
        lost.Result.GetProperty("code").GetString().ShouldBe("claim_lost");
        lost.Result.GetProperty("message").GetString()!.ShouldContain(codexSession);
        lost.Result.GetProperty("current").GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(codexSession);
    }

    [Theory]
    [InlineData("""{"id":"X-1","expected_versoin":1,"title":"t"}""", "expected_versoin")]
    [InlineData("""{"id":"X-1","expected_version":"1","title":"t"}""", "expected_version")]
    [InlineData("""{"id":"X-1","expected_version":1,"status":"ready, in_progress"}""", "status")]
    [InlineData("""{"id":"X-1","expected_version":1,"status":"InProgress"}""", "status")]
    [InlineData("""{"id":"X-1","Expected_Version":1,"title":"t"}""", "Expected_Version")]
    [InlineData("""{"id":"X-1","expected_version":1,"add_links":[{"kind":"pr","link":"https://x"}]}""", "link")]
    public async Task A_misspelt_or_mistyped_argument_is_an_invalid_argument_tool_error(string arguments, string named)
    {
        using var host = await StartHostAsync();
        var agent = await Client.ConnectAsync(host, "claude-agent");

        var call = await agent.CallRawAsync("update_work", arguments);

        call.IsError.ShouldBeTrue();
        call.Result.GetProperty("code").GetString().ShouldBe("invalid_argument");
        call.Result.GetProperty("message").GetString()!.ShouldContain(named);
    }

    [Fact]
    public async Task Without_the_work_store_a_call_is_a_plain_tool_error_with_no_structured_code()
    {
        using var host = await StartHostAsync(new WorkService());
        var agent = await Client.ConnectAsync(host, "claude-agent");

        var call = await agent.CallRawAsync("get_work", """{"id":"SW-1"}""");

        call.IsError.ShouldBeTrue();
        call.Result.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private Task<IHost> StartHostAsync(IWorkService? service = null) =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IMcpHubStore>(_ =>
                    {
                        var store = new InMemoryMcpHubStore();
                        new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync().GetAwaiter().GetResult();
                        return store;
                    });
                    services.Configure<McpOptions>(options => options.RequirePersonalAccessToken = true);
                    services.Configure<AuthOptions>(_ => { });
                    services.AddSingleton<IWorkService>(service ?? db.Service);
                    services.AddMcpServer()
                        .WithHttpTransport()
                        .WithTools(WorkMcpTool.CreateAll());
                })
                .Configure(app =>
                {
                    // Stands in for the PAT authentication handler: a token id (no selection, so
                    // all native tools) and the token owner's username.
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim("mcp_pat_token_id", "42"), new Claim("preferred_username", user.ToString())], "McpPat"));
                        }

                        await next(context);
                    });
                    app.UseRouting();
                    app.UseMiddleware<McpToolEntitlementMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }))
            .StartAsync();

    private sealed class Client(HttpClient http, string user)
    {
        private string? sessionId;
        private int nextId = 1;

        public static async Task<Client> ConnectAsync(IHost host, string user)
        {
            var client = new Client(host.GetTestClient(), user);
            var (status, _, _, session) = await client.PostAsync(
                """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            status.ShouldBe(StatusCodes.Status200OK);
            client.sessionId = session;
            await client.PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            return client;
        }

        public async Task<IReadOnlyList<string>> ListToolsAsync()
        {
            var message = Parse(await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/list\",\"params\":{}}"));
            return message["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        }

        public Task<(bool IsError, JsonElement Result)> CallAsync(string tool, object arguments) =>
            CallRawAsync(tool, JsonSerializer.Serialize(arguments, CodeGraph.Models.Work.WorkJson.Options));

        public async Task<(bool IsError, JsonElement Result)> CallRawAsync(string tool, string arguments)
        {
            var message = Parse(await PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            var result = message["result"] ?? throw new InvalidOperationException($"No result: {message.ToJsonString()}");
            var isError = result["isError"]?.GetValue<bool>() ?? false;
            var structured = JsonSerializer.SerializeToElement(result["structuredContent"]);
            if (!isError)
                // The text block carries the same JSON as the structured content.
                JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.ToJsonString().ShouldBe(result["structuredContent"]!.ToJsonString());
            return (isError, structured);
        }

        private async Task<(int Status, string? ContentType, string Body, string? SessionId)> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("X-Test-User", user);
            if (sessionId is not null)
                request.Headers.Add("Mcp-Session-Id", sessionId);
            using var response = await http.SendAsync(request);
            return (
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync(),
                response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null);
        }

        private static JsonObject Parse((int Status, string? ContentType, string Body, string? SessionId) response)
        {
            if (response.ContentType != "text/event-stream")
                return JsonNode.Parse(response.Body)!.AsObject();

            var data = response.Body.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Single(line => line.StartsWith("data:", StringComparison.Ordinal));
            return JsonNode.Parse(data["data:".Length..])!.AsObject();
        }
    }
}
