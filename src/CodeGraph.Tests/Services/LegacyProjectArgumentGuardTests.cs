using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Tests.Extractors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-27867: "project" used to name a repository on every legacy MCP tool and now means only a
/// multi-repo Project. A stale client that still passes <c>project</c> is refused invalid_argument
/// with a pointer to <c>repository</c> instead of having the argument dropped by the SDK's binding,
/// on the real MCP endpoint, for a tool of every server class; the same call with <c>repository</c>
/// works.
/// </summary>
public sealed class LegacyProjectArgumentGuardTests
{
    private const string Pointer = "`project` now means a multi-repo project; pass `repository`";

    [Theory]
    [InlineData("search_graph", """{"namePattern":"%","project":"orders"}""")]                  // CodeGraphMcpServer
    [InlineData("graph_trace", """{"target":"Place","project":"orders"}""")]                    // ConsolidatedMcpServer
    [InlineData("get_memory_write_status", """{"project":"orders"}""")]                         // MemoryMcpServer
    [InlineData("request_overlay", """{"repo":"orders","ref":"main","project":"orders"}""")]    // OverlayMcpServer
    [InlineData("mysql_list_schemas", """{"project":"orders"}""")]                              // McpHubServer
    public async Task A_call_passing_project_is_refused_invalid_argument_pointing_at_repository(string tool, string arguments)
    {
        using var host = await StartHostAsync();
        var client = await Client.ConnectAsync(host);

        var call = await client.CallAsync(tool, arguments);

        call.IsError.ShouldBeTrue();
        call.Error.GetProperty("code").GetString().ShouldBe("invalid_argument");
        call.Error.GetProperty("message").GetString()!.ShouldStartWith(Pointer);
    }

    [Fact]
    public async Task The_same_calls_with_repository_work()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "orders" });
        await store.UpsertRepositorySummaryAsync("orders", "Takes and ships orders.", Models.ConfidenceLevel.High, "hash", null);
        using var host = await StartHostAsync(store);
        var client = await Client.ConnectAsync(host);

        var direct = await client.CallAsync("get_service_summary", """{"repository":"orders"}""");
        direct.IsError.ShouldBeFalse();
        direct.Text.ShouldContain("Takes and ships orders.");

        var consolidated = await client.CallAsync("repository_report", """{"operation":"summary","repository":"orders"}""");
        consolidated.IsError.ShouldBeFalse();
        consolidated.Text.ShouldContain("Takes and ships orders.");
    }

    [Fact]
    public async Task No_legacy_tool_has_a_project_argument_and_the_repository_tools_say_repository()
    {
        using var host = await StartHostAsync();
        var client = await Client.ConnectAsync(host);

        var tools = await client.ListToolsAsync();

        tools.ShouldNotBeEmpty();
        tools.Where(tool => tool.Properties.Contains("project")).Select(tool => tool.Name).ShouldBeEmpty();
        foreach (var name in new[] { "search_graph", "get_service_summary", "get_architecture", "get_repository_health", "trace_call_path", "analyze_impact", "find_consumers", "find_publishers", "trace_data_lineage" })
            tools.Single(tool => tool.Name == name).Properties.ShouldContain("repository");
        tools.Select(tool => tool.Name).ShouldNotContain("list_projects");
        tools.Select(tool => tool.Name).ShouldNotContain("search_projects");
        tools.Select(tool => tool.Name).ShouldNotContain("project_report");
        tools.Select(tool => tool.Name).ShouldNotContain("get_project_health");
        tools.Select(tool => tool.Name).ShouldContain("list_repositories");
        tools.Select(tool => tool.Name).ShouldContain("search_repositories");
        tools.Select(tool => tool.Name).ShouldContain("repository_report");
        tools.Select(tool => tool.Name).ShouldContain("get_repository_health");
    }

    [Fact]
    public void A_tool_that_declares_project_itself_keeps_it()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { project = new { type = "string" } } });
        var tool = new ModelContextProtocol.Protocol.Tool { Name = "find_work", InputSchema = schema };

        LegacyProjectArgumentGuard.Check(tool, ["project"]).ShouldBeNull();
        LegacyProjectArgumentGuard.Check(tool, ["repository"]).ShouldBeNull();
    }

    private static Task<IHost> StartHostAsync(InMemoryGraphStore? store = null) =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    var graphStore = store ?? new InMemoryGraphStore();
                    services.AddSingleton<IGraphStore>(graphStore);
                    var codegraph = new CodeGraphMcpServer(
                        null!, null!, graphStore, null!, Options.Create(new RepositorySourceOptions()), null!, null!);
                    services.AddSingleton(codegraph);
                    services.AddMcpServer()
                        .WithHttpTransport()
                        .WithTools(codegraph)
                        .WithTools<MemoryMcpServer>()
                        .WithTools<ConsolidatedMcpServer>()
                        .WithTools<McpHubServer>()
                        .WithTools<OverlayMcpServer>()
                        .WithRequestFilters(filters => filters.AddCallToolFilter(LegacyProjectArgumentGuard.Filter));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }))
            .StartAsync();

    private sealed record ToolSummary(string Name, HashSet<string> Properties);

    private sealed record CallResult(bool IsError, string Text, JsonElement Error);

    private sealed class Client(HttpClient http)
    {
        private string? sessionId;
        private int nextId = 1;

        public static async Task<Client> ConnectAsync(IHost host)
        {
            var client = new Client(host.GetTestClient());
            var (status, _, _, session) = await client.PostAsync(
                """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            status.ShouldBe(StatusCodes.Status200OK);
            client.sessionId = session;
            await client.PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            return client;
        }

        public async Task<IReadOnlyList<ToolSummary>> ListToolsAsync()
        {
            var message = Parse(await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/list\",\"params\":{}}"));
            return message["result"]!["tools"]!.AsArray()
                .Select(tool => new ToolSummary(
                    tool!["name"]!.GetValue<string>(),
                    tool["inputSchema"]?["properties"]?.AsObject().Select(property => property.Key).ToHashSet() ?? []))
                .ToList();
        }

        public async Task<CallResult> CallAsync(string tool, string arguments)
        {
            var message = Parse(await PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + nextId++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            var result = message["result"] ?? throw new InvalidOperationException($"No result: {message.ToJsonString()}");
            var isError = result["isError"]?.GetValue<bool>() ?? false;
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            var error = isError ? JsonSerializer.SerializeToElement(JsonNode.Parse(text)) : default;
            return new CallResult(isError, text, error);
        }

        private async Task<(int Status, string? ContentType, string Body, string? SessionId)> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
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
