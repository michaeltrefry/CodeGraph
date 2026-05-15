using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class McpShimTests
{
    private const string Provider = "shortcut-shim";
    private const string DownstreamUrl = "https://downstream.example/mcp";

    // ---- Name mapping --------------------------------------------------------------------

    [Fact]
    public void ShimToolName_PrefixesProviderKey()
    {
        McpShimDiscoveryService.ShimToolName(Provider, "create_story")
            .ShouldBe("shortcut-shim_create_story");
    }

    [Fact]
    public void DownstreamToolName_StripsProviderKeyPrefix()
    {
        var tool = new McpHubToolEntity { ToolName = "shortcut-shim_create_story", ProviderKey = Provider };
        McpShimService.DownstreamToolName(tool).ShouldBe("create_story");
    }

    // ---- Discovery -----------------------------------------------------------------------

    [Fact]
    public async Task Discover_UpsertsDownstreamToolsAsDisabledShimTools()
    {
        var store = await ConfiguredStoreAsync();
        var client = new FakeMcpShimClient
        {
            Tools = [new ShimToolDescriptor("create_story", "Create Story", "Creates a story", "{\"type\":\"object\"}")],
        };
        var sut = new McpShimDiscoveryService(store, client, NullLogger<McpShimDiscoveryService>.Instance);

        var result = await sut.DiscoverAsync(Provider, "admin");

        result.DiscoveredCount.ShouldBe(1);
        result.RetiredCount.ShouldBe(0);
        result.ToolNames.ShouldContain("shortcut-shim_create_story");

        var tool = (await store.ListToolsAsync()).Single(item => item.ToolName == "shortcut-shim_create_story");
        tool.ProviderType.ShouldBe("shim");
        tool.ProviderKey.ShouldBe(Provider);
        // Never silently granted: inactive and not default-selected on first discovery.
        tool.Enabled.ShouldBeFalse();
        tool.DefaultSelected.ShouldBeFalse();
        tool.IsAvailable.ShouldBeTrue();
        tool.InputSchema.ShouldBe("{\"type\":\"object\"}");
    }

    [Fact]
    public async Task Discover_RefreshDoesNotReEnableAnAdminEnabledTool()
    {
        var store = await ConfiguredStoreAsync();
        var client = new FakeMcpShimClient
        {
            Tools = [new ShimToolDescriptor("create_story", null, null, null)],
        };
        var sut = new McpShimDiscoveryService(store, client, NullLogger<McpShimDiscoveryService>.Instance);

        await sut.DiscoverAsync(Provider, "admin");
        // An admin enables the discovered tool.
        await store.UpdateToolCatalogStateAsync("shortcut-shim_create_story", enabled: true, null, null);

        // A second discovery run (e.g. the downstream tool's description changed) must not
        // clobber the admin's choice, and any newly appearing tool still arrives disabled.
        client.Tools =
        [
            new ShimToolDescriptor("create_story", null, "updated description", null),
            new ShimToolDescriptor("delete_story", null, null, null),
        ];
        await sut.DiscoverAsync(Provider, "admin");

        var tools = await store.ListToolsAsync();
        var createStory = tools.Single(item => item.ToolName == "shortcut-shim_create_story");
        createStory.Enabled.ShouldBeTrue();
        createStory.Description.ShouldBe("updated description");
        tools.Single(item => item.ToolName == "shortcut-shim_delete_story").Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Discover_RetiresToolsThatVanishedDownstream()
    {
        var store = await ConfiguredStoreAsync();
        var client = new FakeMcpShimClient
        {
            Tools = [new ShimToolDescriptor("old_tool", null, null, null)],
        };
        var sut = new McpShimDiscoveryService(store, client, NullLogger<McpShimDiscoveryService>.Instance);
        await sut.DiscoverAsync(Provider, "admin");

        client.Tools = [new ShimToolDescriptor("new_tool", null, null, null)];
        var result = await sut.DiscoverAsync(Provider, "admin");

        result.RetiredCount.ShouldBe(1);
        var tools = await store.ListToolsAsync();
        // Retired, not deleted — admin state and entitlements survive a transient downstream change.
        tools.Single(item => item.ToolName == "shortcut-shim_old_tool").IsAvailable.ShouldBeFalse();
        tools.Single(item => item.ToolName == "shortcut-shim_new_tool").IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task Discover_FailsWhenDiscoveryUrlIsNotConfigured()
    {
        var store = new InMemoryMcpHubStore();
        await store.UpsertProviderAsync(NewProvider());
        var sut = new McpShimDiscoveryService(store, new FakeMcpShimClient(), NullLogger<McpShimDiscoveryService>.Instance);

        await Should.ThrowAsync<McpShimDiscoveryException>(() => sut.DiscoverAsync(Provider, "admin"));
    }

    [Fact]
    public async Task Discover_FailsForUnknownProvider()
    {
        var store = new InMemoryMcpHubStore();
        var sut = new McpShimDiscoveryService(store, new FakeMcpShimClient(), NullLogger<McpShimDiscoveryService>.Instance);

        await Should.ThrowAsync<McpShimDiscoveryException>(() => sut.DiscoverAsync("nope", "admin"));
    }

    [Fact]
    public async Task Discover_WrapsDownstreamConnectionFailures()
    {
        var store = await ConfiguredStoreAsync();
        var client = new FakeMcpShimClient { ListThrows = new HttpRequestException("connection refused") };
        var sut = new McpShimDiscoveryService(store, client, NullLogger<McpShimDiscoveryService>.Instance);

        var ex = await Should.ThrowAsync<McpShimDiscoveryException>(() => sut.DiscoverAsync(Provider, "admin"));
        ex.Message.ShouldContain("connection refused");
    }

    // ---- Forwarding ----------------------------------------------------------------------

    [Fact]
    public async Task Forward_CallsDownstreamToolAndAuditsTheInvocation()
    {
        var store = await ConfiguredStoreAsync();
        await store.SetCredentialValueAsync(
            Provider, McpShimDiscoveryService.DiscoveryTokenCredentialKey, "downstream-token", "admin");
        var client = new FakeMcpShimClient
        {
            CallResult = new ShimCallOutcome(
                new JsonObject { ["content"] = new JsonArray(), ["isError"] = false }, IsError: false),
        };
        var sut = new McpShimService(store, client, NullLogger<McpShimService>.Instance);
        var shimTool = ShimTool();

        var result = await sut.ForwardAsync(
            shimTool,
            new Dictionary<string, object?> { ["title"] = "x" },
            "michael",
            tokenId: 7);

        result["isError"]!.GetValue<bool>().ShouldBeFalse();
        client.LastCallTool.ShouldBe("create_story");
        client.LastCallToken.ShouldBe("downstream-token");

        var audit = (await store.ListAuditAsync(10)).Single();
        audit.ProviderType.ShouldBe("shim");
        audit.ProviderKey.ShouldBe(Provider);
        audit.ToolName.ShouldBe("shortcut-shim_create_story");
        audit.Operation.ShouldBe("create_story");
        audit.CredentialMode.ShouldBe("shared");
        audit.AuthorizationDecision.ShouldBe("allowed");
        audit.Success.ShouldBeTrue();
        audit.TokenId.ShouldBe(7);
    }

    [Fact]
    public async Task Forward_ReturnsErrorResultWhenProviderIsNotConfigured()
    {
        var store = new InMemoryMcpHubStore();
        await store.UpsertProviderAsync(NewProvider());
        var sut = new McpShimService(store, new FakeMcpShimClient(), NullLogger<McpShimService>.Instance);

        var result = await sut.ForwardAsync(ShimTool(), null, "michael", 7);

        result["isError"]!.GetValue<bool>().ShouldBeTrue();
        var audit = (await store.ListAuditAsync(10)).Single();
        audit.Success.ShouldBeFalse();
        audit.StatusClass.ShouldBe("provider_error");
    }

    [Fact]
    public async Task Forward_ReturnsErrorResultWhenDownstreamThrows()
    {
        var store = await ConfiguredStoreAsync();
        var client = new FakeMcpShimClient { CallThrows = new HttpRequestException("downstream exploded") };
        var sut = new McpShimService(store, client, NullLogger<McpShimService>.Instance);

        var result = await sut.ForwardAsync(ShimTool(), null, "michael", 7);

        result["isError"]!.GetValue<bool>().ShouldBeTrue();
        result["content"]!.AsArray().Count.ShouldBe(1);
        var audit = (await store.ListAuditAsync(10)).Single();
        audit.Success.ShouldBeFalse();
        audit.StatusClass.ShouldBe("provider_error");
        audit.Message.ShouldContain("downstream exploded");
    }

    // ---- Catalog seeding -----------------------------------------------------------------

    [Fact]
    public async Task CatalogSeeder_SeedsShimProviderAndTagsToolProviderTypes()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();

        (await store.ListProvidersAsync()).ShouldContain(provider => provider.ProviderKey == Provider);

        var tools = await store.ListToolsAsync();
        tools.Single(tool => tool.ToolName == "search_graph").ProviderType.ShouldBe("native");
        tools.Single(tool => tool.ToolName == "codegraph_search").ProviderType.ShouldBe("native");
        tools.Single(tool => tool.ToolName == "shortcut_search_epics").ProviderType.ShouldBe("provider");
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static McpHubProviderEntity NewProvider() => new()
    {
        ProviderKey = Provider,
        DisplayName = "Shortcut (MCP shim)",
        Description = "test",
        Enabled = false,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static McpHubToolEntity ShimTool() => new()
    {
        ToolName = "shortcut-shim_create_story",
        ProviderKey = Provider,
        ProviderType = "shim",
        DisplayName = "Create Story",
        Description = "Creates a story",
        Enabled = true,
        IsAvailable = true,
    };

    private static async Task<InMemoryMcpHubStore> ConfiguredStoreAsync()
    {
        var store = new InMemoryMcpHubStore();
        await store.UpsertProviderAsync(NewProvider());
        await store.SetConfigValueAsync(
            Provider, McpShimDiscoveryService.DiscoveryUrlConfigKey, DownstreamUrl, "admin");
        return store;
    }

    private sealed class FakeMcpShimClient : IMcpShimClient
    {
        public IReadOnlyList<ShimToolDescriptor> Tools { get; set; } = [];
        public ShimCallOutcome? CallResult { get; set; }
        public Exception? ListThrows { get; set; }
        public Exception? CallThrows { get; set; }
        public string? LastCallTool { get; private set; }
        public string? LastCallToken { get; private set; }

        public Task<IReadOnlyList<ShimToolDescriptor>> ListToolsAsync(
            Uri endpoint, string? bearerToken, CancellationToken ct = default)
        {
            if (ListThrows is not null)
                throw ListThrows;
            return Task.FromResult(Tools);
        }

        public Task<ShimCallOutcome> CallToolAsync(
            Uri endpoint, string? bearerToken, string toolName,
            IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct = default)
        {
            LastCallTool = toolName;
            LastCallToken = bearerToken;
            if (CallThrows is not null)
                throw CallThrows;
            return Task.FromResult(CallResult
                ?? new ShimCallOutcome(new JsonObject { ["content"] = new JsonArray() }, IsError: false));
        }
    }
}
