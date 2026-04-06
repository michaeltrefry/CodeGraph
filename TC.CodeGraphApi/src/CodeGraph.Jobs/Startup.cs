using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
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

    public static void ConfigureServices(IServiceCollection services, CodeGraphServiceSettings appSettings)
    {
        services.AddHttpClient();

        if (!appSettings.StorageOptions.IsNeo4j)
        {
            services.AddDbContext<CodeGraphDbContext>(options =>
                options.UseMySql(appSettings.StorageOptions.ConnectionString,
                    ServerVersion.AutoDetect(appSettings.StorageOptions.ConnectionString)));
        }

        services.AddMvc()
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();

        // Settings
        services.AddSingleton(appSettings);
        services.AddSingleton(appSettings.StorageOptions);
        services.AddSingleton(appSettings.AnalysisOptions);
        services.AddSingleton(appSettings.GitLabOptions);

        // Data stores
        if (appSettings.StorageOptions.IsNeo4j)
        {
            services.AddSingleton(new Neo4jSessionFactory(appSettings.StorageOptions));
            services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        }
        else
        {
            services.AddSingleton<IGraphStore, MySqlGraphStore>();
        }

        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddSingleton<IBatchAnalysisService, BatchAnalysisService>();

        // Messaging
        services.AddTransient<IMessageBus, MassTransitMessageBus>();

        var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(rabbitHost, "/", h =>
                {
                    h.Username(Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest");
                    h.Password(Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "guest");
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
        services.AddTransient<IRepoProvider, GitLabRepoProvider>();
        services.AddHttpClient<GitLabRepoProvider>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();
    }

    public static void Configure(WebApplication app)
    {
        app.UseCors();
        app.UseRouting();
        app.MapControllers();
    }
}
