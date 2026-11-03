using CodeGraph.Api;
using CodeGraph.Api.Memory;
using CodeGraph.Memory.Client;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        AssertMemoryScopeBackfillQueue(services, typeof(RemoteMemoryProjectScopeBackfill));
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
        AssertMemoryScopeBackfillQueue(services, typeof(CodeGraph.Data.MariaDb.MySqlMemoryProjectScopeBackfill));
    }

    /// <summary>A project's memory scope backfill (CG-167) is queued and drained by a hosted worker.</summary>
    private static void AssertMemoryScopeBackfillQueue(IServiceCollection services, Type backfill)
    {
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryScopeBackfillQueue));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MemoryScopeBackfillWorker));
        services.Last(d => d.ServiceType == typeof(CodeGraph.Data.IMemoryProjectScopeBackfill)).ImplementationType.ShouldBe(backfill);
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
