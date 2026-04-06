using System.Text.Json.Serialization;
using Autofac;
using Microsoft.EntityFrameworkCore;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Data.Neo4j;
using TC.CodeGraphApi.Services;
using TC.Common.TcServiceStack;
using TC.Common.TcServiceStack.DependencyInjection.AutoFac;
using TC.Jarvis.ApiDocumentation.TcApi;
using TC.Jarvis.Auth.Scopes;
using TC.Jarvis.DependencyInjection;
using TC.CodeGraphJobs.Jobs;
using TC.JobUtilities.DI;
using DiBuilder = TC.Common.TcServiceStack.DependencyInjection.DiBuilder;

namespace TC.CodeGraphJobs;

public class Startup(IWebHostEnvironment env)
    : TcServiceStartup<CodeGraphServiceSettings>(nameof(CodeGraphJobs), Port, env),
        IServiceProviderFactory<IServiceCollection>
{
    public new const int Port = 5038;

    protected override DiBuilder BuildContainer(ContainerBuilder autofac)
    {
        var builder = DiBuilder
            .Init(Using.Autofac(autofac))
            .WithRegistrations(container =>
            {
                if (AppSettings.StorageOptions.IsNeo4j)
                {
                    container.Register(_ => new Neo4jSessionFactory(AppSettings.StorageOptions))
                        .Scoped(Scope.SingleInstance);
                    container.RegisterType<Neo4jGraphStore>().As<IGraphStore>().Scoped(Scope.SingleInstance);
                }
                else
                {
                    container.RegisterType<MySqlGraphStore>().As<IGraphStore>().Scoped(Scope.SingleInstance);
                }
                container.RegisterType<AnthropicCircuitBreaker>().Scoped(Scope.SingleInstance);
                container.RegisterType<BatchAnalysisService>().As<IBatchAnalysisService>().Scoped(Scope.SingleInstance);

                container.AddJobRunner();
                container.RegisterType<DiscoverRepositoriesJob>();
                container.RegisterType<ProcessRepositoriesJob>();
                container.RegisterType<ProcessBatchResultsJob>();

                container.AddTcQueueing(builder =>
                {
                    builder.RegisterEnterpriseBus();
                    builder.RegisterJobsBus();
                });
            });

        return builder;
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseTcApiDocumentationUi();
        DefaultConfigure(app);
    }

    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        AppSettings.LoadEnvironmentOverrides();
        services.AddAuthorization(opts =>
            opts.AddPolicy(
                Scopes.ServiceBusRepublish,
                p => p.AddRequirements(new ResourceScopeRequirement(Scopes.ServiceBusRepublish))
            )
        );

        services.AddHttpClient();

        if (!AppSettings.StorageOptions.IsNeo4j)
        {
            services.AddDbContext<CodeGraphDbContext>(options =>
                options.UseMySql(AppSettings.StorageOptions.ConnectionString,
                    ServerVersion.AutoDetect(AppSettings.StorageOptions.ConnectionString)));
        }

        services
            .AddTcApiDocumentation()
            .AddMvc()
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();

        services.AddSingleton<CodeGraphStorageOptions>(AppSettings.StorageOptions);
        services.AddSingleton<AnalysisOptions>(AppSettings.AnalysisOptions);
        services.AddSingleton<GitLabOptions>(AppSettings.GitLabOptions);

        return ConfigureDefaultContainer(services);
    }
}
