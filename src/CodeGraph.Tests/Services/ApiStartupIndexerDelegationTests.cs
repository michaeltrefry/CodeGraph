using CodeGraph.Api;
using CodeGraph.Api.Indexer;
using CodeGraph.Services.Indexer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ApiStartupIndexerDelegationTests
{
    [Fact]
    public void ConfigureServices_UsesRemoteIndexerOperationsWhenBaseUrlIsConfigured()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration("http://localhost:5042"));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(RemoteIndexerOperationsService));
        services.ShouldNotContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
    }

    [Fact]
    public void ConfigureServices_KeepsLocalIndexerOperationsWhenBaseUrlIsEmpty()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration(""));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(StandaloneIndexerOperationsService));
        services.ShouldContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
    }

    private static IConfiguration CreateConfiguration(string indexerBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:Indexer:BaseUrl"] = indexerBaseUrl,
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
