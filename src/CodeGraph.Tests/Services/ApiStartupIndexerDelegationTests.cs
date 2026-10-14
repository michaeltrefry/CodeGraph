using CodeGraph.Api;
using CodeGraph.Api.Indexer;
using CodeGraph.Services.Indexer;
using Microsoft.Extensions.Configuration;
using CodeGraph.Indexer.Client;
using IJobCommandDispatcher = CodeGraph.Jobs.IJobCommandDispatcher;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ApiStartupIndexerDelegationTests
{
    [Fact]
    public void ConfigureServices_UsesRemoteIndexerOperationsWhenBaseUrlIsConfigured()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration("http://localhost:5042"));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(RemoteIndexerOperationsService));
        services.ShouldNotContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
    }

    [Fact]
    public void ConfigureServices_KeepsLocalIndexerOperationsWhenBaseUrlIsEmpty()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration(""));

        services.ShouldContain(d =>
            d.ServiceType == typeof(IIndexerOperationsService) &&
            d.ImplementationType == typeof(StandaloneIndexerOperationsService));
        services.ShouldContain(d => d.ServiceType == typeof(IIndexerRunBackgroundRunner));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5042")]
    public async Task DevelopmentHost_PassesDependencyInjectionValidation(string indexerBaseUrl)
    {
        // Development turns on ValidateOnBuild/ValidateScopes, so every registration (including
        // the CodeGraph.Jobs jobs and dispatcher that need an IIndexerClient) must resolve.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(Startup).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(CreateConfiguration(indexerBaseUrl));
        Startup.ConfigureServices(builder.Services, builder.Configuration);

        await using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IJobCommandDispatcher>().ShouldNotBeNull();
        var indexerClient = scope.ServiceProvider.GetRequiredService<IIndexerClient>();
        if (string.IsNullOrEmpty(indexerBaseUrl))
            indexerClient.ShouldBeOfType<LocalIndexerClient>();
        else
            indexerClient.ShouldBeOfType<HttpIndexerClient>();
    }

    private static IConfiguration CreateConfiguration(string indexerBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:Indexer:BaseUrl"] = indexerBaseUrl,
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
