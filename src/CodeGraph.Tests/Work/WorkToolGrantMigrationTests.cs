using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Work tools are enabled and pre-selected, except global settings that require an admin's
/// deliberate selection. Existing selected-mode tokens get new tools only by selection.
/// </summary>
public sealed class WorkToolGrantMigrationTests
{
    [Fact]
    public async Task The_catalog_preselects_work_tools_except_global_configuration()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        var tools = (await store.ListToolsAsync()).ToDictionary(tool => tool.ToolName);

        foreach (var definition in WorkTools.All)
        {
            var tool = tools[definition.Name];
            var ordinaryWork = definition.Name != WorkTools.DreamSettings;
            (tool.Enabled, tool.DefaultSelected, tool.IsAvailable, tool.ProviderKey).ShouldBe((true, ordinaryWork, true, "codegraph"), definition.Name);
            tool.ReadOnly.ShouldBe(definition.ReadOnly, definition.Name);
            McpHubExplicitEntitlementPolicy.RequiresExplicitSelection(definition.Name).ShouldBe(!ordinaryWork);
        }
    }
}
