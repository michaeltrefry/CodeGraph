using CodeGraph.Host.Shared.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Indexer.Client;

public static class IndexerClientServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphIndexerClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCodeGraphInternalServiceAuth(configuration);

        services.AddOptions<IndexerClientOptions>()
            .Bind(configuration.GetSection(IndexerClientOptions.SectionPath));

        services.AddHttpClient(IndexerClientOptions.DefaultHttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<IndexerClientOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        });

        services.AddTransient<IIndexerClient, HttpIndexerClient>();
        return services;
    }
}
