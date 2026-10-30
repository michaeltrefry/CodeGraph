using CodeGraph.Extractors.TypeScript;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Work;
using CodeGraph.Services.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeGraph.Tests.IndexerHost;

public class IndexerHostStartupTests
{
    [Fact]
    public async Task DreamingModelGraphResolvesWithoutStartingProviders()
    {
        var services = new ServiceCollection();
        CodeGraph.Indexer.Host.Startup.ConfigureServices(services, CreateConfiguration());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDreamingService>().ShouldBeOfType<DreamingService>();
        scope.ServiceProvider.GetRequiredService<IDreamBatchModel>().ShouldBeOfType<GraphAssistant>();
        scope.ServiceProvider.GetRequiredService<IndexerRunExecutor>().ShouldNotBeNull();
    }

    [Fact]
    public void ConfigureServices_RegistersStandaloneIndexerExecutionSurface()
    {
        var services = new ServiceCollection();

        CodeGraph.Indexer.Host.Startup.ConfigureServices(services, CreateConfiguration());

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(StandaloneIndexerOperationsService));
        services.ShouldContain(d => d.ServiceType == typeof(IndexerRunExecutor));
        services.ShouldContain(d => d.ServiceType == typeof(IDreamingService) && d.ImplementationType == typeof(DreamingService));
        services.ShouldContain(d => d.ServiceType == typeof(IDreamBatchModel));
        services.ShouldContain(d => d.ServiceType == typeof(IDreamSourceReader));
        services.ShouldContain(d => d.ServiceType == typeof(GraphAssistant));
        services.ShouldContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
        services.ShouldContain(d => d.ServiceType == typeof(TypeScriptServerManager));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) &&
                                    d.ImplementationType == typeof(CodeGraph.Indexer.Host.Services.TypeScriptSidecarWarmupService));
        AssertSharedIndexerConsumers(services);
    }

    internal static void AssertSharedIndexerConsumers(IServiceCollection services)
    {
        foreach (var consumerType in SharedConsumerTopology.IndexerConsumerTypes)
            services.ShouldContain(descriptor => descriptor.ServiceType == consumerType);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
