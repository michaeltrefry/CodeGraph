using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeGraph.Mcp.Hub;

public static class McpHubServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphMcpHub(this IServiceCollection services)
    {
        services.AddTransient<McpHubCatalogSeeder>();
        services.AddTransient<McpHubService>();
        services.AddTransient<McpShimDiscoveryService>();
        services.AddTransient<McpShimService>();
        services.TryAddSingleton<IMcpShimClient, McpClientShimClient>();
        services.AddSingleton<SensitiveColumnPolicy>();
        services.AddSingleton<MySqlSourceExposurePolicy>();
        services.AddHostedService<McpHubCatalogHostedService>();
        services.AddHttpClient("mcp-hub-shortcut", client =>
        {
            client.BaseAddress = new Uri("https://api.app.shortcut.com/api/v3/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("mcp-hub-rabbitmq", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }

    /// <summary>
    /// Registers the in-memory, non-durable fallback hub stores — but only for store interfaces
    /// that a durable provider (e.g. MariaDB) has not already registered. Call this AFTER the
    /// persistence provider is registered so the <c>TryAdd</c> calls fill gaps rather than
    /// race the durable registrations. Keeping this separate from <see cref="AddCodeGraphMcpHub"/>
    /// means there is exactly one active registration per store interface — see Shortcut sc-1062.
    /// </summary>
    public static IServiceCollection AddCodeGraphMcpHubInMemoryStoreFallback(this IServiceCollection services)
    {
        services.TryAddSingleton<IMcpHubStore, InMemoryMcpHubStore>();
        services.TryAddSingleton<IMcpSensitiveColumnStore, InMemoryMcpSensitiveColumnStore>();
        services.TryAddSingleton<IMcpProviderCredentialStore, InMemoryMcpProviderCredentialStore>();
        return services;
    }
}
