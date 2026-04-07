using System.Text.Json.Serialization;
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
    public const int Port = 5038;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);
        services.AddHttpClient();

        services.AddMvc()
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();

        // Data stores (Neo4j)
        services.AddSingleton<Neo4jSessionFactory>();
        services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        services.AddSingleton<IFileSystem, LocalFileSystem>();

        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddSingleton<IAnalysisModelProvider, AnthropicAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, OpenAiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, GeminiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, LocalAnalysisProvider>();
        services.AddSingleton<IAnalysisProviderRegistry, AnalysisProviderRegistry>();
        services.AddSingleton<IBatchAnalysisService, BatchAnalysisService>();

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

        // Job framework
        services.AddSingleton<IJobRunner, JobRunner>();
        services.AddTransient<ProcessRepositoriesJob>();
        services.AddTransient<DiscoverRepositoriesJob>();
        services.AddTransient<ProcessBatchResultsJob>();

        // DiscoverRepositoriesJob needs IAdminService — register it and its dependencies
        services.AddTransient<IAdminService, AdminService>();
        services.AddTransient<IExclusionService, ExclusionService>();
        services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
        RegisterRepoProvider(services);
        services.AddHttpClient<GitLabRepoProvider>();
        services.AddHttpClient<GitHubRepoProvider>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();
    }

    public static void Configure(WebApplication app)
    {
        app.UseCors();
        app.UseRouting();
        app.MapControllers();
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
