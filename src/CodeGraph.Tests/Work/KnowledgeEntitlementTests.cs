using System.ComponentModel;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// D17 / R33 (sc-27869): the existing memory tools leave the default MCP listing; the knowledge tools
/// are in it. A default (all-mode) token neither lists nor calls a memory tool; a token whose owner
/// selected one keeps it. The memory tools stay cataloged and enabled, and their data is untouched.
/// </summary>
public sealed class KnowledgeEntitlementTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly string[] KnowledgeTools = [WorkTools.Record, WorkTools.Recall, WorkTools.ImportKnowledge];

    private static IEnumerable<string> ToolNamesOf(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>();

    [Fact]
    public void Every_memory_tool_is_hidden_from_the_default_listing_and_no_other_tool_is()
    {
        var memoryTools = ToolNamesOf(typeof(MemoryMcpServer))
            .Concat(ToolNamesOf(typeof(ConsolidatedMcpServer)).Where(name => name.StartsWith("memory_", StringComparison.Ordinal)))
            .ToList();

        memoryTools.Count.ShouldBe(12);
        McpHubExplicitEntitlementPolicy.HiddenMemoryTools.ShouldBe(memoryTools, ignoreOrder: true);
        foreach (var tool in KnowledgeTools.Concat(["search_graph", "codegraph_search"]))
            McpHubExplicitEntitlementPolicy.RequiresExplicitSelection(tool).ShouldBeFalse(tool);
    }

    [Fact]
    public async Task The_catalog_keeps_the_memory_tools_enabled_but_not_preselected_and_preselects_the_knowledge_tools()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        var tools = (await store.ListToolsAsync()).ToDictionary(tool => tool.ToolName);

        foreach (var name in McpHubExplicitEntitlementPolicy.HiddenMemoryTools)
            (tools[name].Enabled, tools[name].IsAvailable, tools[name].DefaultSelected).ShouldBe((true, true, false), name);
        foreach (var name in KnowledgeTools)
            (tools[name].Enabled, tools[name].DefaultSelected).ShouldBe((true, true), name);
    }

    [Fact]
    public async Task A_default_token_lists_the_knowledge_tools_but_not_the_memory_tools_and_a_token_that_selected_one_keeps_it()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        await store.ReplaceTokenEntitlementsAsync(43, ["query_memory", "memory_read", WorkTools.Recall]);
        using var host = await StartHostAsync(store);

        var all = await McpClient.ConnectAsync(host, tokenId: 42);
        var listed = await all.ListToolsAsync();
        foreach (var tool in KnowledgeTools)
            listed.ShouldContain(tool);
        foreach (var tool in McpHubExplicitEntitlementPolicy.HiddenMemoryTools)
            listed.ShouldNotContain(tool);
        (await all.CallAsync("query_memory"))["error"]!["data"]!["error"]!.GetValue<string>().ShouldBe("tool_not_entitled");

        var selected = await McpClient.ConnectAsync(host, tokenId: 43);
        (await selected.ListToolsAsync()).ShouldBe(["memory_read", "query_memory", WorkTools.Recall], ignoreOrder: true);
        (await selected.CallAsync("query_memory"))["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("called query_memory");
        (await selected.CallAsync("store_memory_v2"))["error"]!["data"]!["error"]!.GetValue<string>().ShouldBe("tool_not_entitled");
    }

    [Fact]
    public async Task Migration_078_only_stops_preselecting_the_memory_tools()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations/078_knowledge_store.sql"));
        var update = script[script.IndexOf("UPDATE mcp_hub_tools", StringComparison.Ordinal)..];
        // Nothing of the memory store is dropped or deleted.
        script.ShouldNotContain("DROP ", Case.Sensitive);
        script.ShouldNotContain("DELETE FROM", Case.Sensitive);
        script.ShouldNotContain("TRUNCATE", Case.Sensitive);

        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync(
            "INSERT INTO mcp_hub_providers (provider_key, display_name, description, enabled, created_at_utc, updated_at_utc) " +
            "VALUES ('codegraph', 'CodeGraph', '', 1, UTC_TIMESTAMP(), UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE enabled = 1");
        // A catalog seeded before 078: both tools pre-selected.
        await connection.ExecuteAsync(
            "INSERT INTO mcp_hub_tools (tool_name, provider_key, display_name, description, enabled, is_available, default_selected, created_at_utc, updated_at_utc) VALUES " +
            "('query_memory', 'codegraph', 'Query Memory', '', 1, 1, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP()), ('search_graph', 'codegraph', 'Search', '', 1, 1, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP()) " +
            "ON DUPLICATE KEY UPDATE enabled = 1, is_available = 1, default_selected = 1");
        await connection.ExecuteAsync(update);

        var rows = (await connection.QueryAsync<(string ToolName, bool Enabled, bool IsAvailable, bool DefaultSelected)>(
            "SELECT tool_name, enabled, is_available, default_selected FROM mcp_hub_tools WHERE tool_name IN ('query_memory', 'search_graph') ORDER BY tool_name")).ToList();
        rows.ShouldBe([("query_memory", true, true, false), ("search_graph", true, true, true)]);
    }

    private static Task<IHost> StartHostAsync(IMcpHubStore store) =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(store);
                    services.Configure<McpOptions>(options => options.RequirePersonalAccessToken = true);
                    services.Configure<AuthOptions>(_ => { });
                    services.AddSingleton<IWorkService>(new WorkService());
                    services.AddMcpServer()
                        .WithHttpTransport()
                        .WithTools(WorkMcpTool.CreateAll())
                        .WithTools<MemoryProbeTools>();
                })
                .Configure(app =>
                {
                    // Stands in for the PAT authentication handler: installs the token-id claim.
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue("X-Test-Token", out var token))
                            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("mcp_pat_token_id", token.ToString())], "McpPat"));
                        await next(context);
                    });
                    app.UseRouting();
                    app.UseMiddleware<McpToolEntitlementMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }))
            .StartAsync();

    /// <summary>Stand-ins for three memory tools, registered under their real names.</summary>
    [McpServerToolType]
    public sealed class MemoryProbeTools
    {
        [McpServerTool(Name = "query_memory"), Description("probe")]
        public static string QueryMemory() => "called query_memory";

        [McpServerTool(Name = "memory_read"), Description("probe")]
        public static string MemoryRead() => "called memory_read";

        [McpServerTool(Name = "store_memory_v2"), Description("probe")]
        public static string StoreMemory() => "called store_memory_v2";
    }

    private sealed class McpClient(HttpClient http, long tokenId)
    {
        private string? sessionId;
        private int nextId = 1;

        public static async Task<McpClient> ConnectAsync(IHost host, long tokenId)
        {
            var client = new McpClient(host.GetTestClient(), tokenId);
            var init = await client.PostAsync(
                """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            init.Status.ShouldBe(StatusCodes.Status200OK);
            client.sessionId = init.SessionId;
            await client.PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            return client;
        }

        public async Task<IReadOnlyList<string>> ListToolsAsync() =>
            Parse(await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/list\",\"params\":{}}"))["result"]!["tools"]!.AsArray()
                .Select(tool => tool!["name"]!.GetValue<string>()).ToList();

        public async Task<JsonObject> CallAsync(string tool) =>
            Parse(await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}"));

        private async Task<(int Status, string? ContentType, string Body, string? SessionId)> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("X-Test-Token", tokenId.ToString());
            if (sessionId is not null)
                request.Headers.Add("Mcp-Session-Id", sessionId);
            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(),
                response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null);
        }

        private static JsonObject Parse((int Status, string? ContentType, string Body, string? SessionId) response)
        {
            if (response.ContentType != "text/event-stream")
                return JsonNode.Parse(response.Body)!.AsObject();
            var data = response.Body.Split('\n').Select(line => line.TrimEnd('\r')).Single(line => line.StartsWith("data:", StringComparison.Ordinal));
            return JsonNode.Parse(data["data:".Length..])!.AsObject();
        }
    }
}
