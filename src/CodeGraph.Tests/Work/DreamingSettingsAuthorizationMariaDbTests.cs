using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class DreamingSettingsAuthorizationMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private MySqlDreamingStore Store() => new(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));

    private ServiceProvider Services() => new ServiceCollection()
        .AddSingleton<IWorkStore>(db.Store).AddSingleton<IDreamingStore>(Store())
        .AddSingleton<IDreamBatchModel, NoModelCalls>().AddSingleton<IDreamSourceReader, NoSourceReads>()
        .AddSingleton<IKnowledgeDreamActions>(db.Service).AddSingleton<IDreamingService, DreamingService>()
        .AddSingleton<IWorkService>(sp => new WorkService(db.Store, db.Clock, serviceProvider: sp)).BuildServiceProvider();

    [Fact]
    public async Task A_web_viewer_reads_settings_and_reports_but_cannot_change_global_budget_or_schedule()
    {
        using var services = Services();
        await using var host = await WorkRestHost.StartAsync(services.GetRequiredService<IWorkService>(), db.Store);
        var project = await db.NewProjectAsync("DA");
        var path = $"/api/work/dreaming/{project}";
        var current = await Store().GetSettingsAsync();
        var body = SaveBody(current with { MaxTokensPerRun = 1_000_000, Schedule = "* * * * *" });
        (await host.SendAsync(HttpMethod.Get, path + "/settings", person: "viewer")).Status.ShouldBe(200);
        foreach (var key in new[] { project, "another-project" })
        {
            var (status, refused) = await host.SendAsync(HttpMethod.Put, $"/api/work/dreaming/{key}/settings", body, person: "viewer");
            status.ShouldBe(403); refused.GetProperty("code").GetString().ShouldBe("forbidden");
            (await Store().GetSettingsAsync()).ShouldBe(current);
        }
        var (runStatus, run) = await host.SendAsync(HttpMethod.Post, path + "/runs", "{}", person: "viewer");
        runStatus.ShouldBe(200); run.GetProperty("status").GetString().ShouldBe("skipped");
        (await host.SendAsync(HttpMethod.Get, path + "/runs", person: "viewer")).Status.ShouldBe(200);
        (await host.SendAsync(HttpMethod.Get, path + "/runs/" + run.GetProperty("id").GetGuid(), person: "viewer")).Status.ShouldBe(200);
        // Instructions inherit the existing conventions-wiki editing policy, not global-settings administration.
        var (_, instructions) = await host.SendAsync(HttpMethod.Get, path + "/instructions", person: "viewer");
        var revision = instructions.GetProperty("base").GetProperty("revision").GetInt32();
        (await host.SendAsync(HttpMethod.Put, path + "/instructions", JsonSerializer.Serialize(new
        { scope = "base", content = "Read evidence before verification.", expected_revision = revision }), person: "viewer")).Status.ShouldBe(200);
        (await host.SendAsync(HttpMethod.Put, path + "/settings", body, person: "admin")).Status.ShouldBe(200);
        var saved = await Store().GetSettingsAsync();
        saved.MaxTokensPerRun.ShouldBe(1_000_000); saved.Schedule.ShouldBe("* * * * *");
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<string>("SELECT cron_expression FROM job_schedules WHERE job_type='DreamProjects' LIMIT 1")).ShouldBe(saved.Schedule);
    }

    [Fact]
    public async Task Self_service_cannot_launder_a_viewer_into_global_configuration_access_but_admin_can_delegate()
    {
        using var services = Services();
        await using var host = await WorkRestHost.StartAsync(services.GetRequiredService<IWorkService>(), db.Store, enableMcp: true);
        var current = await Store().GetSettingsAsync();
        var body = SaveBody(current with { MaxTurns = current.MaxTurns + 1 });
        const string settingsPath = "/api/work/dreaming/any-project/settings";
        var create = JsonSerializer.Serialize(new { name = "configuration agent", expiresInDays = 30, toolNames = new[] { WorkTools.DreamSettings } });
        (await host.SendAsync(HttpMethod.Post, "/api/user/mcp-tokens", create, person: "viewer")).Status.ShouldBe(403);
        (await host.SendAsync(HttpMethod.Post, "/api/user/mcp-tokens",
            """{"name":"normalized grant","expiresInDays":30,"toolNames":[" DREAM_SETTINGS "]}""", person: "viewer")).Status.ShouldBe(403);
        var (_, tools) = await host.SendAsync(HttpMethod.Get, "/api/user/mcp-tokens/tools", person: "viewer");
        tools.EnumerateArray().ShouldNotContain(tool => tool.GetProperty("toolName").GetString() == WorkTools.DreamSettings);

        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/user/mcp-tokens", """{"name":"all tools","expiresInDays":30}""", person: "viewer");
        var allToken = created.GetProperty("rawToken").GetString()!;
        var tokenId = created.GetProperty("token").GetProperty("id").GetInt64();
        var edit = JsonSerializer.Serialize(new { toolNames = new[] { WorkTools.DreamSettings } });
        (await host.SendAsync(HttpMethod.Put, $"/api/user/mcp-tokens/{tokenId}/tools", edit, person: "viewer")).Status.ShouldBe(403);
        (await host.SendAsync(HttpMethod.Put, $"/api/user/mcp-tokens/{tokenId}/tools",
            """{"toolNames":[" DREAM_SETTINGS "]}""", person: "viewer")).Status.ShouldBe(403);
        (await host.SendAsync(HttpMethod.Put, settingsPath, body, allToken)).Status.ShouldBe(403);
        var deniedToken = await host.CreateTokenAsync("reader-agent", WorkTools.DreamRuns);
        (await host.SendAsync(HttpMethod.Put, settingsPath, body, deniedToken)).Status.ShouldBe(403);
        (await Store().GetSettingsAsync()).ShouldBe(current);

        var allMcp = await McpClient.ConnectAsync(host.Client, allToken);
        (await allMcp.CallAsync(new { settings = current, expected_version = current.Version }))["error"]!["data"]!["error"]!.GetValue<string>().ShouldBe("tool_not_entitled");
        var deniedMcp = await McpClient.ConnectAsync(host.Client, deniedToken);
        (await deniedMcp.CallAsync(new { settings = current, expected_version = current.Version }))["error"]!["data"]!["error"]!.GetValue<string>().ShouldBe("tool_not_entitled");
        (await Store().GetSettingsAsync()).ShouldBe(current);

        var (grantStatus, grant) = await host.SendAsync(HttpMethod.Post, "/api/user/mcp-tokens", create, person: "admin");
        grantStatus.ShouldBe(200);
        var (_, adminTools) = await host.SendAsync(HttpMethod.Get, "/api/user/mcp-tokens/tools", person: "admin");
        adminTools.EnumerateArray().Single(tool => tool.GetProperty("toolName").GetString() == WorkTools.DreamSettings)
            .GetProperty("defaultSelected").GetBoolean().ShouldBeFalse();
        var delegated = grant.GetProperty("rawToken").GetString()!;
        // The PAT carries no web admin claims: its explicit entitlement alone authorizes the agent.
        (await host.SendAsync(HttpMethod.Put, settingsPath, body, delegated)).Status.ShouldBe(200);
        var afterRest = await Store().GetSettingsAsync();
        var allowedMcp = await McpClient.ConnectAsync(host.Client, delegated);
        var response = await allowedMcp.CallAsync(new { settings = afterRest with { MaxTurns = afterRest.MaxTurns + 1 }, expected_version = afterRest.Version });
        (response["result"]?["isError"]?.GetValue<bool>() ?? false).ShouldBeFalse(response.ToJsonString());
        response["result"]!["structuredContent"]!["max_turns"]!.GetValue<int>().ShouldBe(afterRest.MaxTurns + 1);
        (await Store().GetSettingsAsync()).MaxTurns.ShouldBe(afterRest.MaxTurns + 1);
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 403)]
    public async Task Auth_disabled_respects_the_existing_local_dev_admin_option(bool localAdmin, int expectedStatus)
    {
        using var services = Services();
        await using var host = await WorkRestHost.StartAsync(services.GetRequiredService<IWorkService>(), db.Store,
            configureServices: s => s.Configure<AuthOptions>(o => { o.Enabled = false; o.LocalDevIsAdmin = localAdmin; }));
        var current = await Store().GetSettingsAsync();
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/work/dreaming/dev/settings", SaveBody(current), person: "local-dev");
        status.ShouldBe(expectedStatus);
        if (!localAdmin) (await Store().GetSettingsAsync()).ShouldBe(current);
    }

    [Fact]
    public async Task MariaDb_tokens_require_selected_mode_and_an_explicit_settings_grant()
    {
        await using var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(db.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb)).Options);
        var hub = new MySqlMcpHubStore(context, null!); // Entitlement checks do not use credential encryption.
        var token = new McpPersonalAccessTokenEntity { Username = "agent", TokenName = "settings", TokenPrefixValue = "cgmcp_", TokenHash = Guid.NewGuid().ToString("N"), LastFour = "1234", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), EntitlementMode = "all" };
        context.McpPersonalAccessTokens.Add(token); await context.SaveChangesAsync();
        (await hub.IsTokenEntitledAsync(token.Id, WorkTools.DreamSettings)).ShouldBeFalse();
        await hub.ReplaceTokenEntitlementsAsync(token.Id, [WorkTools.DreamSettings]);
        (await hub.IsTokenEntitledAsync(token.Id, WorkTools.DreamSettings)).ShouldBeFalse();
        token.EntitlementMode = "selected"; await context.SaveChangesAsync();
        (await hub.IsTokenEntitledAsync(token.Id, WorkTools.DreamSettings)).ShouldBeTrue();
        await hub.ReplaceTokenEntitlementsAsync(token.Id, [WorkTools.DreamRuns]);
        (await hub.IsTokenEntitledAsync(token.Id, WorkTools.DreamSettings)).ShouldBeFalse();
    }

    private static string SaveBody(DreamSettings settings) => JsonSerializer.Serialize(new { settings, expected_version = settings.Version }, WorkJson.Options);

    private sealed class NoModelCalls : IDreamBatchModel
    {
        public bool SupportsTools(string provider) => true;
        public Task<DreamModelResponse> CompleteAsync(DreamModelRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Authorization tests must not call a model.");
    }

    private sealed class NoSourceReads : IDreamSourceReader
    {
        public Task<object> ExecuteAsync(Guid projectId, string tool, JsonElement arguments, CancellationToken ct = default) =>
            throw new InvalidOperationException("Authorization tests must not read source.");
    }

    private sealed class McpClient(HttpClient http, string token)
    {
        private string? session;
        private int id = 1;

        public static async Task<McpClient> ConnectAsync(HttpClient http, string token)
        {
            var client = new McpClient(http, token);
            await client.PostAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"settings-authorization","version":"1"}}}""");
            await client.PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            return client;
        }

        public async Task<JsonNode> CallAsync(object arguments) => JsonNode.Parse(await PostAsync(JsonSerializer.Serialize(new
        { jsonrpc = "2.0", id = id++, method = "tools/call", @params = new { name = WorkTools.DreamSettings, arguments } }, WorkJson.Options)))!;

        private async Task<string> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
            using var response = await http.SendAsync(request);
            response.IsSuccessStatusCode.ShouldBeTrue();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.First();
            var text = await response.Content.ReadAsStringAsync();
            return response.Content.Headers.ContentType?.MediaType == "text/event-stream"
                ? text.Split('\n').Select(line => line.TrimEnd('\r')).Single(line => line.StartsWith("data:", StringComparison.Ordinal))[5..]
                : text;
        }
    }
}
