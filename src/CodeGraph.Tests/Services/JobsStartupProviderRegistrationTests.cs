using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using CodeGraph.Jobs;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Tests.Services;

public class JobsStartupProviderRegistrationTests
{
    [Theory]
    [InlineData("anthropic", "anthropic")]
    [InlineData("openai", "openai")]
    [InlineData("gemini", "gemini")]
    [InlineData("local", "local")]
    public void ConfigureServices_RegistersRequestedAnalysisProvider(string defaultProvider, string expectedProvider)
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = defaultProvider,
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp"
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAnalysisProviderRegistry>();

        registry.GetProvider().ProviderName.ShouldBe(expectedProvider);
    }
}
