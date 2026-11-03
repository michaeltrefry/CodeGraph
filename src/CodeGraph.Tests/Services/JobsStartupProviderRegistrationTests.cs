using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using CodeGraph.Indexer.Client;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Jobs;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Memory.Client;
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

    [Theory]
    [InlineData(null, typeof(MySqlMemoryProjectScopeBackfill))]
    [InlineData("http://codegraph-memory:5039", typeof(RemoteMemoryProjectScopeBackfill))]
    public async Task ConfigureServices_BackfillsMemoryScopesWhereMemoryLives(string? memoryBaseUrl, Type expected)
    {
        // The daily reconciliation (CG-167) runs here: with a memory host, memory is on its database.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:Memory:BaseUrl"] = memoryBaseUrl,
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMemoryProjectScopeBackfill>().ShouldBeOfType(expected);
        scope.ServiceProvider.GetRequiredService<ReconcileMemoryProjectScopesJob>().ShouldNotBeNull();
    }
}
