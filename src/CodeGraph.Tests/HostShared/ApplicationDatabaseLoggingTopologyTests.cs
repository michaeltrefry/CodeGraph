using CodeGraph.Data;
using CodeGraph.Host.Shared.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

public class ApplicationDatabaseLoggingTopologyTests
{
    [Theory]
    [InlineData("api", ApplicationLogServices.Api)]
    [InlineData("indexer", ApplicationLogServices.Indexer)]
    [InlineData("jobs", ApplicationLogServices.Jobs)]
    [InlineData("memory", ApplicationLogServices.Memory)]
    [InlineData("metrics", ApplicationLogServices.Metrics)]
    public void MariaDbHosts_RegisterDatabaseLoggerWithStableServiceName(string host, string expectedService)
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        switch (host)
        {
            case "api":
                CodeGraph.Api.Startup.ConfigureServices(services, configuration);
                break;
            case "indexer":
                CodeGraph.Indexer.Host.Startup.ConfigureServices(services, configuration);
                break;
            case "jobs":
                CodeGraph.Jobs.Startup.ConfigureServices(services, configuration);
                break;
            case "memory":
                CodeGraph.Memory.Host.Startup.ConfigureServices(services, configuration);
                break;
            case "metrics":
                CodeGraph.Metrics.Startup.ConfigureServices(services, configuration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(host));
        }

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(ILoggerProvider)
            && descriptor.ImplementationType == typeof(ApplicationDatabaseLoggerProvider));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<ApplicationDatabaseLoggingOptions>>()
            .Value.ServiceName.ShouldBe(expectedService);
    }

    [Fact]
    public void Registration_RejectsUnknownServiceName()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            services.AddApplicationDatabaseLogging(CreateConfiguration(), "database"));
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
