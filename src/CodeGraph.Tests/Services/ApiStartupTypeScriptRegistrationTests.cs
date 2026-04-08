using CodeGraph.Api;
using CodeGraph.Extractors.TypeScript;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ApiStartupTypeScriptRegistrationTests
{
    [Fact]
    public void ConfigureServices_RegistersTypeScriptServerManagerAsSingleton()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp"
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        var descriptor = services.Single(d => d.ServiceType == typeof(TypeScriptServerManager));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }
}
