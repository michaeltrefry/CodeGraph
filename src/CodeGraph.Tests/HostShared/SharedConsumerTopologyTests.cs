using CodeGraph.Host.Shared.Consumers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

public class SharedConsumerTopologyTests
{
    [Fact]
    public void IntegratedApiAndSplitIndexer_RegisterTheSameIndexerConsumerTypes()
    {
        var apiServices = new ServiceCollection();
        CodeGraph.Api.Startup.ConfigureServices(apiServices, CreateConfiguration());

        var indexerServices = new ServiceCollection();
        CodeGraph.Indexer.Host.Startup.ConfigureServices(indexerServices, CreateConfiguration());

        var apiTypes = RegisteredSharedIndexerTypes(apiServices);
        var indexerTypes = RegisteredSharedIndexerTypes(indexerServices);

        apiTypes.ShouldBe(SharedConsumerTopology.IndexerConsumerTypes, ignoreOrder: true);
        indexerTypes.ShouldBe(SharedConsumerTopology.IndexerConsumerTypes, ignoreOrder: true);
        apiTypes.ShouldBe(indexerTypes, ignoreOrder: true);
        apiTypes.ShouldAllBe(type => type.Assembly == typeof(SharedConsumerTopology).Assembly);
    }

    [Fact]
    public void ApiWithRemoteIndexer_DoesNotRegisterIndexerConsumers()
    {
        var services = new ServiceCollection();
        CodeGraph.Api.Startup.ConfigureServices(services, CreateConfiguration(remoteIndexer: true));

        RegisteredSharedIndexerTypes(services).ShouldBeEmpty();
    }

    [Fact]
    public void SharedTopology_DeclaresEveryIndexerEndpointExactlyOnce()
    {
        SharedConsumerTopology.IndexerEndpointNames.ShouldBe(
        [
            "process-repository",
            "repository-indexing-completed",
            "analysis-batch-submitted",
            "project-analysis-results-processed",
            "analysis-synthesis-completed",
            "repository-removed"
        ]);
        SharedConsumerTopology.IndexerEndpointNames.Distinct().Count()
            .ShouldBe(SharedConsumerTopology.IndexerEndpointNames.Count);
    }

    private static Type[] RegisteredSharedIndexerTypes(IServiceCollection services) =>
        SharedConsumerTopology.IndexerConsumerTypes
            .Where(type => services.Any(descriptor => descriptor.ServiceType == type))
            .ToArray();

    private static IConfiguration CreateConfiguration(bool remoteIndexer = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
            ["CodeGraph:RepositorySource:Provider"] = "Folder",
            ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
            ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
            ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
            ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
        };
        if (remoteIndexer)
            values["CodeGraph:Indexer:BaseUrl"] = "http://indexer:5038";

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
