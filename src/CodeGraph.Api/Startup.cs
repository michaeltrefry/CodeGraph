using System.Text.Json.Serialization;
using Anthropic;
using CodeGraph.Api.Consumers;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Extractors.Sql;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Jobs;
using ModelContextProtocol.AspNetCore;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Services.Reviews;

namespace CodeGraph.Api;

public static class Startup
{
    public const int Port = 5037;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);

        services.AddHttpClient();

        services
            .AddMvc()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}", "http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = "CodeGraph API",
                Version = "v1",
                Description = "Knowledge graph indexing and query API for source repositories"
            });
        });

        // Data stores (Neo4j)
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

        // Embeddings + semantic search
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
        services.AddTransient<ISemanticSearchService, SemanticSearchService>();
        services.AddTransient<GraphQueryEngine>();
        services.AddTransient<IndexingPipeline>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<GraphAssistant>();
        services.AddTransient<CodeGraphDocGenerator>();

        // Solution-level Roslyn analysis
        services.AddTransient<ISolutionAnalyzer, SolutionAnalyzer>();

        // TypeScript/Angular analysis (Node.js sidecar)
        services.AddSingleton(sp => new TypeScriptServerManager(
            port: sp.GetRequiredService<IOptions<CodeGraphServiceSettings>>().Value.TsPort,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<TypeScriptServerManager>()));
        services.AddTransient<ITypeScriptAnalyzer, TypeScriptProjectAnalyzer>();

        // Lint / Trust scoring
        services.AddSingleton<LintResultCache>();
        services.AddSingleton<DiagnosticDetailCache>();
        services.AddTransient<ILintRunner>(sp => new CompositeLintRunner(
            sp.GetRequiredService<LintResultCache>(),
            sp.GetRequiredService<TypeScriptServerManager>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CompositeLintRunner>()));

        // NuGet reference extraction
        services.AddTransient<INuGetReferenceExtractor, NuGetReferenceExtractor>();

        // Code extractors
        services.AddTransient<ICodeExtractor, RoslynExtractor>();
        services.AddTransient<ICodeExtractor, SqlExtractor>();
        services.AddTransient<ICodeExtractor, TypeScriptExtractor>();
        services.AddTransient<ICodeExtractor, TreeSitterExtractor>();

        // File system
        services.AddSingleton<IFileSystem, LocalFileSystem>();

        // Vitals + security
        services.AddTransient<IVitalsAnalyzer, VitalsAnalyzer>();
        services.AddSingleton<ISourceFileProvider, FileSystemSourceFileProvider>();
        services.AddTransient<ISecurityAnalyzer, SecurityAnalyzer>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();
        services.AddTransient<IImpactAnalysisService, ImpactAnalysisService>();

        // AI analyzer
        services.AddSingleton(_ => new AnthropicClient());
        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddSingleton<IAnalysisModelProvider, AnthropicAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, OpenAiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, GeminiAnalysisProvider>();
        services.AddSingleton<IAnalysisModelProvider, LocalAnalysisProvider>();
        services.AddSingleton<IAnalysisProviderRegistry, AnalysisProviderRegistry>();
        services.AddTransient<IBatchAnalysisService, BatchAnalysisService>();
        services.AddTransient<IProjectService, ProjectService>();
        services.AddTransient<IProjectQueryService, ProjectQueryService>();
        services.AddSingleton<IProjectReviewBackgroundRunner, ProjectReviewBackgroundRunner>();
        services.AddSingleton<IRepositoryReviewBackgroundRunner, RepositoryReviewBackgroundRunner>();
        services.AddTransient<IRepositoryReviewRecoveryService, RepositoryReviewRecoveryService>();
        services.AddTransient<ProjectReviewService>();
        services.AddTransient<IProjectReviewService>(sp => sp.GetRequiredService<ProjectReviewService>());
        services.AddTransient<RepositoryReviewService>();
        services.AddTransient<IRepositoryReviewService>(sp => sp.GetRequiredService<RepositoryReviewService>());
        services.AddTransient<IAdminService, AdminService>();
        services.AddTransient<IWikiService, WikiService>();
        services.AddTransient<IWikiSectionSeedService, WikiSectionSeedService>();
        services.AddTransient<IAttachmentService, AttachmentService>();
        services.AddTransient<IMcpDocService, McpDocService>();
        services.AddTransient<IGraphOverviewService, GraphOverviewService>();
        services.AddTransient<INodeQueryService, NodeQueryService>();
        services.AddTransient<ISearchService, SearchService>();
        services.AddTransient<IExclusionService, ExclusionService>();
        services.AddHttpClient<GitLabRepoProvider>();
        services.AddHttpClient<GitHubRepoProvider>();
        RegisterRepoProvider(services);
        services.AddCodeGraphJobScheduling();

        // Memory graph
        services.AddTransient<MemoryClaimIngestionService>();
        services.AddTransient<MemoryLegacyMigrationService>();
        services.AddTransient<MemoryObservationMigrationService>();
        services.AddTransient<MemoryRetrievalService>();
        services.AddTransient<MemoryService>();

        // Messaging — IMessageBus wraps MassTransit IPublishEndpoint
        services.AddTransient<IMessageBus, MassTransitMessageBus>();

        // MassTransit + RabbitMQ
        services.AddMassTransit(x =>
        {
            x.AddDelayedMessageScheduler();
            x.AddConsumer<ProcessRepositoryConsumer>();
            x.AddConsumer<RepositoryIndexingCompletedConsumer>();
            x.AddConsumer<AnalysisBatchSubmittedConsumer>();
            x.AddConsumer<ProjectAnalysisResultsProcessedConsumer>();
            x.AddConsumer<AnalysisSynthesisCompletedConsumer>();
            x.AddConsumer<RepositoryRemovedConsumer>();
            x.AddConsumer<StoreMemoryClaimsConsumer>();

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

                cfg.ReceiveEndpoint("store-memory-claims", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<StoreMemoryClaimsConsumer>(context);
                });
            });
        });

        // MCP server
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "codegraph",
                Version = "1.0.0"
            };
        })
        .WithHttpTransport()
        .WithTools<CodeGraphMcpServer>()
        .WithTools<MemoryMcpServer>()
        .WithResources<CodeGraphMcpResources>();
    }

    public static void Configure(WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeGraph API v1"));
        app.UseCors();
        app.UseRouting();
        app.MapControllers();
        app.MapMcp("/mcp");
    }

    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        var hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
        var storageOptions = serviceProvider.GetRequiredService<IOptions<CodeGraphStorageOptions>>().Value;
        var migrationRunner = serviceProvider.GetRequiredService<IMigrationRunner>();
        var migrationsPath = ResolveMigrationsPath(hostEnvironment.ContentRootPath, storageOptions.Neo4jMigrationsPath);
        await migrationRunner.ApplyMigrationsAsync(migrationsPath);

        var wikiSectionSeedService = serviceProvider.GetRequiredService<IWikiSectionSeedService>();
        await wikiSectionSeedService.EnsureDefaultSectionsAsync();

        var exclusionService = serviceProvider.GetRequiredService<IExclusionService>();
        var repoSourceOptions = serviceProvider.GetRequiredService<IOptions<RepositorySourceOptions>>().Value;
        await exclusionService.SeedFromConfigAsync(repoSourceOptions.ExcludedGroups);

        var repositoryReviewRecoveryService = serviceProvider.GetRequiredService<IRepositoryReviewRecoveryService>();
        await repositoryReviewRecoveryService.RecoverInterruptedRunsAsync();
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

    private static string ResolveMigrationsPath(string contentRootPath, string migrationsPath)
    {
        if (Path.IsPathRooted(migrationsPath))
            return migrationsPath;

        return Path.GetFullPath(Path.Combine(contentRootPath, migrationsPath));
    }
}
