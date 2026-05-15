using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Mcp.Hub;

public sealed class McpHubCatalogHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<McpHubCatalogHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var seeder = scope.ServiceProvider.GetRequiredService<McpHubCatalogSeeder>();
            await seeder.EnsureCatalogAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to seed MCP Hub catalog");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
