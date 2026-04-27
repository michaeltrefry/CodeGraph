using CodeGraph.Extractors.TypeScript;
using CodeGraph.Services.Indexer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeGraph.Tests.IndexerHost;

public class IndexerHostStartupTests
{
    [Fact]
    public void ConfigureServices_RegistersStandaloneIndexerExecutionSurface()
    {
        var services = new ServiceCollection();

        CodeGraph.Indexer.Host.Startup.ConfigureServices(services, CreateConfiguration());

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(StandaloneIndexerOperationsService));
        services.ShouldContain(d => d.ServiceType == typeof(IndexerRunExecutor));
        services.ShouldContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
        services.ShouldContain(d => d.ServiceType == typeof(TypeScriptServerManager));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) &&
                                    d.ImplementationType == typeof(CodeGraph.Indexer.Host.Services.TypeScriptSidecarWarmupService));
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
