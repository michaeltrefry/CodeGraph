using CodeGraph.Memory.Host.Consumers;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.MemoryHost;

public class MemoryHostStartupTests
{
    [Fact]
    public void ConfigureServices_RegistersStandaloneMemorySurface()
    {
        var services = new ServiceCollection();

        CodeGraph.Memory.Host.Startup.ConfigureServices(services, CreateConfiguration());

        services.ShouldContain(d => d.ServiceType == typeof(MemoryService));
        services.ShouldContain(d => d.ServiceType == typeof(MemoryClaimIngestionService));
        services.ShouldContain(d => d.ServiceType == typeof(MemoryRetrievalService));
        services.ShouldContain(d => d.ServiceType == typeof(IEmbeddingService) &&
                                    d.ImplementationType == typeof(OnnxEmbeddingService));
        services.ShouldContain(d => d.ServiceType == typeof(StoreMemoryClaimsConsumer));
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
