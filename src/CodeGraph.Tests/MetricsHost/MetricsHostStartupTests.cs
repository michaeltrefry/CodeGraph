using CodeGraph.Metrics.Consumers;
using CodeGraph.Services.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.MetricsHost;

public class MetricsHostStartupTests
{
    [Fact]
    public void ConfigureServices_RegistersStandaloneMetricsSurface()
    {
        var services = new ServiceCollection();

        CodeGraph.Metrics.Startup.ConfigureServices(services, CreateConfiguration());

        services.ShouldContain(d => d.ServiceType == typeof(IMetricsEventRecorder) &&
                                    d.ImplementationType == typeof(MetricsEventRecorder));
        services.ShouldContain(d => d.ServiceType == typeof(LlmUsageRecordedConsumer));
        services.ShouldContain(d => d.ServiceType == typeof(McpToolInvocationRecordedConsumer));
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
