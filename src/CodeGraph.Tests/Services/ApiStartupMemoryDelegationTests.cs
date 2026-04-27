using CodeGraph.Api;
using CodeGraph.Api.Memory;
using CodeGraph.Memory.Client;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ApiStartupMemoryDelegationTests
{
    [Fact]
    public void ConfigureServices_UsesRemoteMemoryOperationsWhenBaseUrlIsConfigured()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration("http://localhost:5039"));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IMemoryOperationsService) &&
            d.ImplementationType == typeof(RemoteMemoryOperationsService));
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryClient));
    }

    [Fact]
    public void ConfigureServices_KeepsLocalMemoryOperationsWhenBaseUrlIsEmpty()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration(""));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IMemoryOperationsService) &&
            d.ImplementationType == typeof(LocalMemoryOperationsService));
        services.ShouldNotContain(d => d.ServiceType == typeof(IMemoryClient));
    }

    private static IConfiguration CreateConfiguration(string memoryBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:Memory:BaseUrl"] = memoryBaseUrl,
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
