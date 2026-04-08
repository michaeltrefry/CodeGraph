using MassTransit;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Jobs;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);
        services.AddHttpClient();

        // Data stores (Neo4j)
        services.AddSingleton<Neo4jSessionFactory>();
        services.AddTransient<IGraphStore, Neo4jGraphStore>();
        services.AddTransient<IJobScheduleStore, Neo4jJobScheduleStore>();
        services.AddTransient<IWikiStore, Neo4jWikiStore>();
        services.AddTransient<IDbHealthStore>(sp => sp.GetRequiredService<IGraphStore>() as IDbHealthStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IDbHealthStore"));
        services.AddSingleton<IFileSystem, LocalFileSystem>();

        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddSingleton<IAnalysisModelProvider, AnthropicAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, OpenAiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, GeminiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, LocalAnalysisProvider>();
        services.AddSingleton<IAnalysisProviderRegistry, AnalysisProviderRegistry>();
        services.AddTransient<IBatchAnalysisService, BatchAnalysisService>();

        // Messaging
        services.AddTransient<IMessageBus, MassTransitMessageBus>();

        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitOptions = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                cfg.Host(rabbitOptions.Host, "/", h =>
                {
                    h.Username(rabbitOptions.Username);
                    h.Password(rabbitOptions.Password);
                });
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddTransient<IAdminService, AdminService>();
        services.AddTransient<IExclusionService, ExclusionService>();
        services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
        services.AddTransient<IMcpDocService, McpDocService>();
        RegisterRepoProvider(services);
        services.AddHttpClient<GitLabRepoProvider>();
        services.AddHttpClient<GitHubRepoProvider>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();
        services.AddCodeGraphJobScheduling();
        services.AddHostedService<ScheduleRunnerWorker>();
    }

    private static void RegisterRepoProvider(IServiceCollection services)
    {
        services.AddTransient<IRepoProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RepositorySourceOptions>>().Value;
            return options.Provider switch
            {
                RepositorySourceProvider.GitHub => sp.GetRequiredService<GitHubRepoProvider>(),
                RepositorySourceProvider.Folder => sp.GetRequiredService<FolderRepoProvider>(),
                _ => sp.GetRequiredService<GitLabRepoProvider>()
            };
        });
    }
}
