using System.Security.Claims;
using System.Text.Json.Serialization;
using Anthropic;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Extractors.Ansible;
using CodeGraph.Extractors.ColdFusion;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Extractors.Sql;
using CodeGraph.Extractors.Terraform;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Host.Shared.Hosting;
using CodeGraph.Indexer.Host.Consumers;
using CodeGraph.Indexer.Host.Services;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.DatabaseSchema;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Prompts;
using Microsoft.Extensions.Options;
using MassTransit;

namespace CodeGraph.Indexer.Host;

public static class Startup
{
    public const int Port = 5042;
    public const string InternalServiceAudience = "codegraph-indexer";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);
        services.AddCodeGraphHostShared(configuration, "CodeGraph.Indexer.Host");
        services.AddHttpClient();

        services
            .AddMvc()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = "CodeGraph Indexer Host",
                Version = "v1",
                Description = "Standalone indexing execution host for CodeGraph"
            });
        });

        RegisterPersistence(services, configuration);
        RegisterIndexerServices(services);
        RegisterMassTransit(services);
    }

    public static void Configure(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeGraph Indexer Host v1"));
        }

        app.UseCors();
        app.UseRouting();
        app.UseInternalServiceAuthentication();
        app.MapControllers();
        app.MapHealthChecks("/health");
    }

    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        var hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
        var storageOptions = serviceProvider.GetRequiredService<IOptions<CodeGraphStorageOptions>>().Value;
        var migrationRunner = serviceProvider.GetRequiredService<IMigrationRunner>();
        var configuredMigrationsPath = IsMariaDbProvider(storageOptions)
            ? storageOptions.MariaDbMigrationsPath
            : storageOptions.Neo4jMigrationsPath;
        var migrationsPath = ResolveMigrationsPath(hostEnvironment.ContentRootPath, configuredMigrationsPath);
        await migrationRunner.ApplyMigrationsAsync(migrationsPath);

        var exclusionService = serviceProvider.GetRequiredService<IExclusionService>();
        var repoSourceOptions = serviceProvider.GetRequiredService<IOptions<RepositorySourceOptions>>().Value;
        await exclusionService.SeedFromConfigAsync(repoSourceOptions.ExcludedGroups);
    }

    private static void RegisterIndexerServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new TypeScriptServerManager(
            port: sp.GetRequiredService<IOptions<CodeGraphServiceSettings>>().Value.TsPort,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<TypeScriptServerManager>()));
        services.AddHostedService<TypeScriptSidecarWarmupService>();
        services.AddTransient<ITypeScriptAnalyzer, TypeScriptProjectAnalyzer>();

        services.AddSingleton<LintResultCache>();
        services.AddSingleton<DiagnosticDetailCache>();
        services.AddTransient<ILintRunner>(sp => new CompositeLintRunner(
            sp.GetRequiredService<LintResultCache>(),
            sp.GetRequiredService<TypeScriptServerManager>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CompositeLintRunner>()));

        services.AddTransient<ISolutionAnalyzer, SolutionAnalyzer>();
        services.AddTransient<INuGetReferenceExtractor, NuGetReferenceExtractor>();
        services.AddTransient<ICodeExtractor, RoslynExtractor>();
        services.AddTransient<ICodeExtractor, SqlExtractor>();
        services.AddTransient<ICodeExtractor, AnsibleExtractor>();
        services.AddTransient<ICodeExtractor, ColdFusionExtractor>();
        services.AddTransient<ICodeExtractor, TerraformExtractor>();
        services.AddTransient<ICodeExtractor, TypeScriptExtractor>();
        services.AddTransient<ICodeExtractor, TreeSitterExtractor>();

        services.AddSingleton<IFileSystem, LocalFileSystem>();
        services.AddSingleton<ISourceFileProvider, FileSystemSourceFileProvider>();
        services.AddTransient<IndexingPipeline>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<IVitalsAnalyzer, VitalsAnalyzer>();
        services.AddTransient<ISecurityAnalyzer, SecurityAnalyzer>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();

        services.AddSingleton(_ => new AnthropicClient());
        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddScoped<IAnalysisModelProvider, AnthropicAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, OpenAiAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, GeminiAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, LocalAnalysisProvider>();
        services.AddScoped<IAnalysisProviderRegistry, AnalysisProviderRegistry>();
        services.AddTransient<IAgentPromptService, AgentPromptService>();
        services.AddTransient<IMetricsEventPublisher, MetricsEventPublisher>();
        services.AddTransient<IBatchAnalysisService, BatchAnalysisService>();
        services.AddTransient<IProjectService, ProjectService>();

        services.AddTransient<IExclusionService, ExclusionService>();
        services.AddTransient<IAdminService, AdminService>();
        services.AddTransient<IDatabaseSchemaExtractor, DatabaseSchemaExtractor>();
        services.AddTransient<IndexerRunExecutor>();
        services.AddSingleton<IIndexerRunBackgroundRunner, IndexerRunBackgroundRunner>();
        services.AddTransient<IIndexerOperationsService, StandaloneIndexerOperationsService>();

        RegisterRepoProvider(services);
        services.AddHttpClient<GitLabRepoProvider>();
        services.AddHttpClient<GitHubRepoProvider>();
        services.AddTransient<IMessageBus, MassTransitMessageBus>();
    }

    private static void RegisterMassTransit(IServiceCollection services)
    {
        services.AddMassTransit(x =>
        {
            x.AddDelayedMessageScheduler();
            x.AddConsumer<ProcessRepositoryConsumer>();
            x.AddConsumer<RepositoryIndexingCompletedConsumer>();
            x.AddConsumer<AnalysisBatchSubmittedConsumer>();
            x.AddConsumer<ProjectAnalysisResultsProcessedConsumer>();
            x.AddConsumer<AnalysisSynthesisCompletedConsumer>();
            x.AddConsumer<RepositoryRemovedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.UseDelayedMessageScheduler();

                var rabbitOptions = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                cfg.Host(rabbitOptions.Host, "/", h =>
                {
                    h.Username(rabbitOptions.Username);
                    h.Password(rabbitOptions.Password);
                });

                var consumerOptions = context.GetRequiredService<IOptions<ConsumerOptions>>().Value;
                cfg.ReceiveEndpoint("process-repository", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<ProcessRepositoryConsumer>(context);
                });
                cfg.ReceiveEndpoint("repository-indexing-completed", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<RepositoryIndexingCompletedConsumer>(context);
                });
                cfg.ReceiveEndpoint("analysis-batch-submitted", e =>
                {
                    e.ConcurrentMessageLimit = 1;
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<AnalysisBatchSubmittedConsumer>(context);
                });
                cfg.ReceiveEndpoint("project-analysis-results-processed", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<ProjectAnalysisResultsProcessedConsumer>(context);
                });
                cfg.ReceiveEndpoint("analysis-synthesis-completed", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<AnalysisSynthesisCompletedConsumer>(context);
                });
                cfg.ReceiveEndpoint("repository-removed", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<RepositoryRemovedConsumer>(context);
                });
            });
        });
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

    private static void RegisterPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var storageOptions = configuration
            .GetSection($"{CodeGraphOptionsServiceCollectionExtensions.SectionName}:{nameof(CodeGraphServiceSettings.StorageOptions)}")
            .Get<CodeGraphStorageOptions>() ?? new CodeGraphStorageOptions();

        if (IsMariaDbProvider(storageOptions))
        {
            services.AddCodeGraphMariaDbData(options =>
            {
                options.ConnectionString = storageOptions.MariaDbConnectionString;
                options.MigrationsPath = storageOptions.MariaDbMigrationsPath;
                options.EncryptionKey = storageOptions.MariaDbEncryptionKey;
            });
            return;
        }

        services.AddSingleton<Neo4jSessionFactory>();
        services.AddTransient<IGraphStore, Neo4jGraphStore>();
        services.AddTransient<IMigrationRunner>(sp => sp.GetRequiredService<IGraphStore>());
        services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
        services.AddTransient<IDbHealthStore>(sp => sp.GetRequiredService<IGraphStore>() as IDbHealthStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IDbHealthStore"));
        services.AddTransient<IJobScheduleStore, Neo4jJobScheduleStore>();
        services.AddTransient<IVectorStore, Neo4jVectorStore>();
        services.AddTransient<IWikiStore, Neo4jWikiStore>();
        services.AddTransient<IMemoryGraphStore, Neo4jMemoryGraphStore>();
    }

    private static bool IsMariaDbProvider(CodeGraphStorageOptions storageOptions) =>
        storageOptions.Provider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase)
        || storageOptions.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

    private static string ResolveMigrationsPath(string contentRootPath, string migrationsPath)
    {
        if (Path.IsPathRooted(migrationsPath))
            return migrationsPath;

        var contentRelativePath = Path.GetFullPath(Path.Combine(contentRootPath, migrationsPath));
        if (Directory.Exists(contentRelativePath))
            return contentRelativePath;

        var directory = new DirectoryInfo(contentRootPath);
        while (directory.Parent is not null)
        {
            directory = directory.Parent;
            var ancestorRelativePath = Path.GetFullPath(Path.Combine(directory.FullName, migrationsPath));
            if (Directory.Exists(ancestorRelativePath))
                return ancestorRelativePath;
        }

        return contentRelativePath;
    }
}

internal static class InternalServiceAuthenticationApplicationBuilderExtensions
{
    public static IApplicationBuilder UseInternalServiceAuthentication(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method) ||
                context.Request.Path.StartsWithSegments("/health") ||
                context.Request.Path.StartsWithSegments("/swagger"))
            {
                await next();
                return;
            }

            var authOptions = context.RequestServices.GetRequiredService<IOptions<InternalServiceAuthOptions>>().Value;
            if (!authOptions.Enabled)
            {
                context.User = CreatePrincipal("local-indexer", "LocalInternalService");
                await next();
                return;
            }

            var validator = context.RequestServices.GetRequiredService<IInternalServiceTokenValidator>();
            var token = context.Request.Headers[authOptions.HeaderName].ToString();
            var validation = validator.ValidateToken(token, Startup.InternalServiceAudience);
            if (!validation.IsValid)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized", message = validation.Error });
                return;
            }

            context.User = validation.Principal ?? CreatePrincipal("unknown", "CodeGraphInternalService");
            await next();
        });
    }

    private static ClaimsPrincipal CreatePrincipal(string username, string authenticationType)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim("preferred_username", username),
            new Claim("codegraph_internal", "true")
        ], authenticationType));
    }
}
