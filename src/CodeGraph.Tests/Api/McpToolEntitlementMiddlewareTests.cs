using System.Security.Claims;
using System.Text;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Api;

public class McpToolEntitlementMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_FiltersToolsList_ToEnabledEntitledCatalogTools()
    {
        var store = new RecordingMcpHubStore
        {
            Providers =
            [
                new() { ProviderKey = "codegraph", Enabled = true },
                new() { ProviderKey = "mysql", Enabled = true },
                new() { ProviderKey = "shortcut", Enabled = false }
            ],
            Tools =
            [
                new() { ToolName = "search_graph", ProviderKey = "codegraph", Enabled = true },
                new() { ToolName = "mysql_readonly_query", ProviderKey = "mysql", Enabled = true },
                new() { ToolName = "shortcut_search_epics", ProviderKey = "shortcut", Enabled = true },
                new() { ToolName = "disabled_tool", ProviderKey = "codegraph", Enabled = false }
            ]
        };
        store.Entitlements[42] = ["search_graph"];

        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var middleware = CreateMiddleware(
            context => context.Response.WriteAsync("""
                {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"search_graph"},{"name":"mysql_readonly_query"},{"name":"shortcut_search_epics"},{"name":"disabled_tool"},{"name":"not_cataloged"}]}}
                """),
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
            """);
        context.User = PrincipalWithToken(42);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("\"search_graph\"");
        body.ShouldNotContain("mysql_readonly_query");
        body.ShouldNotContain("shortcut_search_epics");
        body.ShouldNotContain("disabled_tool");
        body.ShouldNotContain("not_cataloged");
    }

    [Fact]
    public async Task InvokeAsync_RejectsToolCall_WhenProviderIsDisabled()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "shortcut", Enabled = false }],
            Tools = [new() { ToolName = "shortcut_search_epics", ProviderKey = "shortcut", Enabled = true }]
        };

        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var middleware = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"shortcut_search_epics","arguments":{}}}
            """);
        context.User = PrincipalWithToken(42);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("provider_disabled");
    }

    [Fact]
    public async Task InvokeAsync_RejectsToolCall_WhenNoTokenId_AndPatRequired()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "codegraph", Enabled = true }],
            Tools = [new() { ToolName = "search_graph", ProviderKey = "codegraph", Enabled = true }]
        };
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var nextCalled = false;
        var middleware = CreateMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            requiresPat: true);

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_graph","arguments":{}}}
            """);
        // No PAT principal — context.User has no mcp_pat_token_id claim.

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("token_required");
    }

    [Fact]
    public async Task InvokeAsync_AllowsToolCall_WhenNoTokenId_AndPatNotRequired()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "codegraph", Enabled = true }],
            Tools = [new() { ToolName = "search_graph", ProviderKey = "codegraph", Enabled = true }]
        };
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var nextCalled = false;
        var middleware = CreateMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            requiresPat: false);

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_graph","arguments":{}}}
            """);

        await middleware.InvokeAsync(context);

        // Open mode (no PAT system configured) — the request passes through untouched.
        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_FiltersToolsListToEmpty_WhenNoTokenId_AndPatRequired()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "codegraph", Enabled = true }],
            Tools = [new() { ToolName = "search_graph", ProviderKey = "codegraph", Enabled = true }]
        };
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var middleware = CreateMiddleware(
            context => context.Response.WriteAsync("""
                {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"search_graph"}]}}
                """),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            requiresPat: true);

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
            """);
        // No PAT principal.

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldNotContain("search_graph");
    }

    [Fact]
    public async Task InvokeAsync_RejectsToolCall_WhenToolIsUnavailable()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "codegraph", Enabled = true }],
            Tools = [new() { ToolName = "search_graph", ProviderKey = "codegraph", Enabled = true, IsAvailable = false }]
        };
        store.Entitlements[42] = ["search_graph"];
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IMcpHubStore>(store)
            .BuildServiceProvider(validateScopes: true);

        var middleware = CreateMiddleware(
            _ => Task.CompletedTask,
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var context = CreateMcpContext("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_graph","arguments":{}}}
            """);
        context.User = PrincipalWithToken(42);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("tool_unavailable");
    }

    private static McpToolEntitlementMiddleware CreateMiddleware(
        RequestDelegate next,
        IServiceScopeFactory scopeFactory,
        bool requiresPat = true) =>
        new(
            next,
            scopeFactory,
            Options.Create(new McpOptions { RequirePersonalAccessToken = requiresPat }),
            Options.Create(new AuthOptions()),
            NullLogger<McpToolEntitlementMiddleware>.Instance);

    private static DefaultHttpContext CreateMcpContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static ClaimsPrincipal PrincipalWithToken(long tokenId) =>
        new(new ClaimsIdentity([new Claim("mcp_pat_token_id", tokenId.ToString())], "McpPat"));

    private sealed class RecordingMcpHubStore : IMcpHubStore
    {
        public IReadOnlyList<McpHubProviderEntity> Providers { get; init; } = [];
        public IReadOnlyList<McpHubToolEntity> Tools { get; init; } = [];
        public Dictionary<long, IReadOnlyList<string>> Entitlements { get; } = [];

        public Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default) => Task.FromResult(Providers);
        public Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult(Tools);
        public Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(!Entitlements.TryGetValue(tokenId, out var names) || names.Contains(toolName, StringComparer.OrdinalIgnoreCase));

        public Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateToolCatalogStateAsync(string toolName, bool? enabled, bool? defaultSelected, string? accessClass, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
