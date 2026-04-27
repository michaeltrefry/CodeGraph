using CodeGraph.Host.Shared.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Memory.Client;

public static class MemoryClientServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphMemoryClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCodeGraphInternalServiceAuth(configuration);

        services.AddOptions<MemoryClientOptions>()
            .Bind(configuration.GetSection(MemoryClientOptions.SectionPath));

        services.AddHttpClient(MemoryClientOptions.DefaultHttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryClientOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        });

        services.AddTransient<IMemoryClient, HttpMemoryClient>();
        return services;
    }
}
