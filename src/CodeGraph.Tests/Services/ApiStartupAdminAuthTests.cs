using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using CodeGraph.Api;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Tests.Services;

public class ApiStartupAdminAuthTests
{
    [Fact]
    public async Task ConfigureServices_RegistersCoreServicesWithoutAdminAuth()
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

        services.Any(d => d.ServiceType.Name == "AdminAccessFilter").ShouldBeFalse();
        services.Any(d => d.ServiceType.Name == "IAdminUserService").ShouldBeFalse();
        services.Any(d => d.ServiceType.Name == "IAdminStore").ShouldBeFalse();

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAnalysisProviderRegistry>().GetProvider().ProviderName.ShouldBe("anthropic");
    }
}
