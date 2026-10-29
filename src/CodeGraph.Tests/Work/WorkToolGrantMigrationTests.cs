using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The work tools are in the hub catalog, enabled and pre-selected for new tokens. Existing
/// selected-mode tokens get them only when an owner selects them.
/// </summary>
public sealed class WorkToolGrantMigrationTests
{
    [Fact]
    public async Task The_catalog_lists_every_work_tool_enabled_and_selected_by_default()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        var tools = (await store.ListToolsAsync()).ToDictionary(tool => tool.ToolName);

        foreach (var definition in WorkTools.All)
        {
            var tool = tools[definition.Name];
            (tool.Enabled, tool.DefaultSelected, tool.IsAvailable, tool.ProviderKey).ShouldBe((true, true, true, "codegraph"), definition.Name);
            tool.ReadOnly.ShouldBe(definition.ReadOnly, definition.Name);
            McpHubExplicitEntitlementPolicy.RequiresExplicitSelection(definition.Name).ShouldBeFalse();
        }
    }
}
