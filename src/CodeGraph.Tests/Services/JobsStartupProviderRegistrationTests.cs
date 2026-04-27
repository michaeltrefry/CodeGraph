using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using CodeGraph.Indexer.Client;
using CodeGraph.Jobs;
using CodeGraph.Services.Configuration;
using CodeGraph.Services;

namespace CodeGraph.Tests.Services;

public class JobsStartupProviderRegistrationTests
{
    [Fact]
    public async Task ConfigureServices_RegistersIndexerClientWithoutAnalysisExecutionGraph()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;"
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IIndexerClient>().ShouldNotBeNull();
        services.ShouldNotContain(d => d.ServiceType.Name == "IAnalysisProviderRegistry");
        services.ShouldNotContain(d => d.ServiceType == typeof(IAdminService));
    }

    [Fact]
    public async Task ConfigureServices_ValidatesMariaDbScopedProviderGraph()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;"
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IIndexerClient>().ShouldNotBeNull();
    }
}
