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
    public async Task ConfigureServices_RegistersRequestedAnalysisProvider(string defaultProvider, string expectedProvider)
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = defaultProvider,
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;"
            })
            .Build();

        Startup.ConfigureServices(services, configuration);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAnalysisProviderRegistry>();

        registry.GetProvider().ProviderName.ShouldBe(expectedProvider);
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
        scope.ServiceProvider.GetRequiredService<IAnalysisProviderRegistry>().ShouldNotBeNull();
    }
}
