using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Shouldly;

namespace CodeGraph.Tests.Api;

/// <summary>
/// Drives the real MCP SDK streamable-HTTP endpoint (stateful, as in production) through
/// <see cref="McpToolEntitlementMiddleware"/> with the real catalog seeding, so assertions see the
/// exact wire format a hosted client sees: tools/list arrives SSE-framed.
/// </summary>
public sealed class McpToolEntitlementEndToEndTests
{
    private static readonly string[] NewTools =
    [
        OverlayMcpServer.RequestOverlayTool,
        OverlayMcpServer.GetOverlayStatusTool,
        CodeGraphMcpServer.AnalyzeImpactFileTool,
        CodeGraphMcpServer.FileHotspotTool,
    ];

    private static readonly string[] ProbeToolNames =
        [.. NewTools, "search_graph", "stories-upload-file", "not_cataloged"];

    [Fact]
    public async Task ToolsList_ForAllModeToken_ListsExactlyTheCallableTools_IncludingTheNewOnes()
    {
        var store = await CreateSeededStoreAsync();
        using var host = await StartHostAsync(store);
        var client = await McpClient.ConnectAsync(host, tokenId: 42);

        var listed = await client.ListToolsAsync();

        listed.ShouldBe([.. NewTools, "search_graph"], ignoreOrder: true);
        await AssertListMatchesCallsAsync(client, listed);
    }

    [Fact]
    public async Task ToolsList_ForSelectedToken_ListsOnlyItsSelection()
    {
        var store = await CreateSeededStoreAsync();
        await store.ReplaceTokenEntitlementsAsync(43, ["search_graph"]);
        await store.ReplaceTokenEntitlementsAsync(44, ["search_graph", .. NewTools]);
        using var host = await StartHostAsync(store);

        var without = await McpClient.ConnectAsync(host, tokenId: 43);
        var withoutListed = await without.ListToolsAsync();
        withoutListed.ShouldBe(["search_graph"]);
        await AssertListMatchesCallsAsync(without, withoutListed);

        var with = await McpClient.ConnectAsync(host, tokenId: 44);
        var withListed = await with.ListToolsAsync();
        withListed.ShouldBe([.. NewTools, "search_graph"], ignoreOrder: true);
        await AssertListMatchesCallsAsync(with, withListed);
    }

    [Fact]
    public async Task DeniedToolCall_IsAJsonRpcError_EchoingTheRequestId()
    {
        var store = await CreateSeededStoreAsync();
        await store.ReplaceTokenEntitlementsAsync(43, ["search_graph"]);
        using var host = await StartHostAsync(store);
        var client = await McpClient.ConnectAsync(host, tokenId: 43);

        var response = await client.PostAsync(
            """{"jsonrpc":"2.0","id":"req-7","method":"tools/call","params":{"name":"request_overlay","arguments":{}}}""");

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.ContentType.ShouldBe("application/json");
        var message = JsonNode.Parse(response.Body)!.AsObject();
        message["jsonrpc"]!.GetValue<string>().ShouldBe("2.0");
        message["id"]!.GetValue<string>().ShouldBe("req-7");
        message.ContainsKey("result").ShouldBeFalse();
        message["error"]!["code"]!.GetValue<int>().ShouldBe(McpToolEntitlementMiddleware.ToolAccessDeniedErrorCode);
        message["error"]!["message"]!.GetValue<string>().ShouldContain("request_overlay");
        message["error"]!["data"]!["error"]!.GetValue<string>().ShouldBe("tool_not_entitled");
        message["error"]!["data"]!["tool"]!.GetValue<string>().ShouldBe("request_overlay");

        var numeric = await client.PostAsync(
            """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"file_hotspot","arguments":{}}}""");
        JsonNode.Parse(numeric.Body)!["id"]!.GetValue<int>().ShouldBe(31);
    }

    [Theory]
    [InlineData("list_projects", "list_repositories")]
    [InlineData("search_projects", "search_repositories")]
    [InlineData("project_report", "repository_report")]
    [InlineData("get_project_health", "get_repository_health")]
    public async Task CallToAToolRenamedAwayFromProject_IsInvalidArgumentNamingTheReplacement_BeforeTheEntitlementDenial(
        string oldName, string newName)
    {
        var store = await CreateSeededStoreAsync();
        // A selected-mode token with no grant for either name, and an all-mode token: both get the
        // same answer, and neither gains access to the replacement.
        await store.ReplaceTokenEntitlementsAsync(43, ["search_graph"]);
        using var host = await StartHostAsync(store);

        foreach (var tokenId in new long[] { 42, 43 })
        {
            var client = await McpClient.ConnectAsync(host, tokenId);
            var response = await client.PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"r-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + oldName + "\",\"arguments\":{}}}");

            response.Status.ShouldBe(StatusCodes.Status200OK);
            var message = JsonNode.Parse(response.Body)!.AsObject();
            message["id"]!.GetValue<string>().ShouldBe("r-1");
            message.ContainsKey("error").ShouldBeFalse();
            message["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
            var error = JsonNode.Parse(message["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
            error["code"]!.GetValue<string>().ShouldBe("invalid_argument");
            error["message"]!.GetValue<string>().ShouldBe(
                $"`{oldName}` was renamed to `{newName}`; `project` now means a multi-repo project");

            // The refusal grants nothing: the replacement is still its own, separately entitled tool,
            // and this host's catalog does not carry it, so the call is not served.
            var replacement = await client.PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + newName + "\",\"arguments\":{}}}");
            var replacementMessage = McpClient.ParseMessage(replacement);
            (replacementMessage["error"] is not null || replacementMessage["result"]?["isError"]?.GetValue<bool>() == true).ShouldBeTrue();
        }
    }

    // Every listed tool is callable and every unlisted probe tool is refused: list and call agree.
    private static async Task AssertListMatchesCallsAsync(McpClient client, IReadOnlyCollection<string> listed)
    {
        var id = 100;
        foreach (var name in ProbeToolNames)
        {
            var response = await client.PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + id++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":{}}}");
            var message = McpClient.ParseMessage(response);
            var denied = message["error"]?["data"]?["error"] is not null;
            denied.ShouldBe(!listed.Contains(name), $"{name}: listed={listed.Contains(name)} response={response.Body}");
            if (!denied)
                message["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("called " + name);
        }
    }

    private static async Task<IMcpHubStore> CreateSeededStoreAsync()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        // Explicit-only tools are never granted to an all-mode token; enable its provider so the
        // entitlement rule (not a disabled provider) is what hides it.
        await store.SetProviderEnabledAsync("shortcut", enabled: true, sourceVisible: null);
        await store.UpdateToolCatalogStateAsync("stories-upload-file", enabled: true, defaultSelected: null, accessClass: null);
        return store;
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
                    services.AddMcpServer()
                        .WithHttpTransport()
                        .WithTools<ProbeTools>();
                })
                .Configure(app =>
                {
                    // Stands in for the PAT authentication handler: installs the token-id claim.
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue("X-Test-Token", out var token))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim("mcp_pat_token_id", token.ToString())], "McpPat"));
                        }
                        await next(context);
                    });
                    app.UseRouting();
                    app.UseMiddleware<McpToolEntitlementMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }))
            .StartAsync();

    private sealed class McpClient(HttpClient http, long tokenId)
    {
        private string? sessionId;

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

        public async Task<IReadOnlyList<string>> ListToolsAsync()
        {
            var response = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
            response.Status.ShouldBe(StatusCodes.Status200OK);
            response.ContentType.ShouldBe("text/event-stream");
            return ParseMessage(response)["result"]!["tools"]!.AsArray()
                .Select(tool => tool!["name"]!.GetValue<string>())
                .ToList();
        }

        public async Task<(int Status, string? ContentType, string Body, string? SessionId)> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("X-Test-Token", tokenId.ToString());
            if (sessionId is not null)
                request.Headers.Add("Mcp-Session-Id", sessionId);
            using var response = await http.SendAsync(request);
            return (
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync(),
                response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null);
        }

        public static JsonObject ParseMessage((int Status, string? ContentType, string Body, string? SessionId) response)
        {
            if (response.ContentType != "text/event-stream")
                return JsonNode.Parse(response.Body)!.AsObject();

            var data = response.Body.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Single(line => line.StartsWith("data:", StringComparison.Ordinal));
            return JsonNode.Parse(data["data:".Length..])!.AsObject();
        }
    }

    [McpServerToolType]
    public sealed class ProbeTools
    {
        [McpServerTool(Name = "search_graph"), Description("probe")]
        public static string SearchGraph() => "called search_graph";

        [McpServerTool(Name = "request_overlay"), Description("probe")]
        public static string RequestOverlay() => "called request_overlay";

        [McpServerTool(Name = "get_overlay_status"), Description("probe")]
        public static string GetOverlayStatus() => "called get_overlay_status";

        [McpServerTool(Name = "analyze_impact_file"), Description("probe")]
        public static string AnalyzeImpactFile() => "called analyze_impact_file";

        [McpServerTool(Name = "file_hotspot"), Description("probe")]
        public static string FileHotspot() => "called file_hotspot";

        [McpServerTool(Name = "stories-upload-file"), Description("probe")]
        public static string StoriesUploadFile() => "called stories-upload-file";

        [McpServerTool(Name = "not_cataloged"), Description("probe")]
        public static string NotCataloged() => "called not_cataloged";
    }
}
