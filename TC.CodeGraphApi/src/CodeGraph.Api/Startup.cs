using System.Text.Json.Serialization;
using Anthropic;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Consumers;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Extractors.ColdFusion;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Extractors.Sql;
using CodeGraph.Extractors.Ansible;
using CodeGraph.Extractors.Terraform;
using CodeGraph.Extractors.TypeScript;
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

namespace CodeGraph.Api;

public static class Startup
{
    public const int Port = 5037;

    public static void ConfigureServices(IServiceCollection services, CodeGraphServiceSettings appSettings)
    {
        // Auth
        services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("Admin", p => p.AddRequirements(new AdminRequirement()));
        });
        services.AddHttpClient();

        // EF Core (MySQL)
        if (!appSettings.StorageOptions.IsNeo4j)
        {
            services.AddDbContext<CodeGraphDbContext>(options =>
                options.UseMySql(appSettings.StorageOptions.ConnectionString,
                    ServerVersion.AutoDetect(appSettings.StorageOptions.ConnectionString)));
        }

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
                Description = "Knowledge graph indexing and query API for ~620 GitLab repositories"
            });
        });

        // Settings — singleton options objects
        services.AddSingleton(appSettings);
        services.AddSingleton(appSettings.StorageOptions);
        services.AddSingleton(appSettings.AnalysisOptions);
        services.AddSingleton(appSettings.GitLabOptions);
        services.AddSingleton(appSettings.IndexingOptions);
        services.AddSingleton(appSettings.WikiOptions);
        services.AddSingleton(appSettings.AuthOptions);
        services.AddSingleton(appSettings.ConsumerOptions);

        // Data stores
        if (appSettings.StorageOptions.IsNeo4j)
        {
            services.AddSingleton(new Neo4jSessionFactory(appSettings.StorageOptions));
            services.AddTransient<IGraphStore, Neo4jGraphStore>();
            services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
                ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
            services.AddTransient<IVectorStore, Neo4jVectorStore>();
            services.AddTransient<IWikiStore, Neo4jWikiStore>();
            services.AddTransient<IAdminStore, Neo4jAdminStore>();
            services.AddTransient<IMemoryGraphStore, Neo4jMemoryGraphStore>();
        }
        else
        {
            services.AddTransient<IGraphStore, MySqlGraphStore>();
            services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
                ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
            services.AddSingleton<IVectorStore, NullVectorStore>();
            services.AddTransient<IWikiStore, MySqlWikiStore>();
            services.AddTransient<IAdminStore, MySqlAdminStore>();
            services.AddSingleton<IMemoryGraphStore, NullMemoryGraphStore>();
        }

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
        services.AddTransient(_ => new TypeScriptServerManager(port: appSettings.TsPort,
            _.GetRequiredService<ILoggerFactory>().CreateLogger<TypeScriptServerManager>()));
        services.AddTransient<ITypeScriptAnalyzer, TypeScriptProjectAnalyzer>();

        // Lint / Trust scoring
        services.AddSingleton<LintResultCache>();
        services.AddTransient<ILintRunner>(sp => new CompositeLintRunner(
            sp.GetRequiredService<LintResultCache>(),
            sp.GetRequiredService<TypeScriptServerManager>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CompositeLintRunner>()));

        // NuGet reference extraction
        services.AddTransient<INuGetReferenceExtractor, NuGetReferenceExtractor>();

        // Code extractors
        services.AddTransient<ICodeExtractor, RoslynExtractor>();
        services.AddTransient<ICodeExtractor, SqlExtractor>();
        services.AddTransient<ICodeExtractor, ColdFusionExtractor>();
        services.AddTransient<ICodeExtractor, TypeScriptExtractor>();
        services.AddTransient<ICodeExtractor, AnsibleExtractor>();
        services.AddTransient<ICodeExtractor, TerraformExtractor>();

        // File system
        services.AddSingleton<IFileSystem, LocalFileSystem>();

        // Vitals + security
        services.AddTransient<IVitalsAnalyzer, VitalsAnalyzer>();
        services.AddSingleton<ISourceFileProvider, FileSystemSourceFileProvider>();
        services.AddTransient<ISecurityAnalyzer, SecurityAnalyzer>();
        services.AddTransient<ICommunityDetectionService, CommunityDetectionService>();
        services.AddTransient<IImpactAnalysisService, ImpactAnalysisService>();

        // Claude analyzer
        services.AddSingleton(_ => new AnthropicClient());
        services.AddSingleton<AnthropicCircuitBreaker>();
        services.AddTransient<ICodeAnalyzer, ClaudeCodeAnalyzer>();
        services.AddTransient<IBatchAnalysisService, BatchAnalysisService>();
        services.AddTransient<IRepoProvider, GitLabRepoProvider>();
        services.AddTransient<IProjectService, ProjectService>();
        services.AddTransient<IProjectQueryService, ProjectQueryService>();
        services.AddTransient<IAdminService, AdminService>();
        services.AddTransient<IAdminUserService, AdminUserService>();
        services.AddTransient<ISettingsService, SettingsService>();
        services.AddTransient<IWikiService, WikiService>();
        services.AddTransient<IAttachmentService, AttachmentService>();
        services.AddTransient<IMcpDocService, McpDocService>();
        services.AddTransient<IGraphOverviewService, GraphOverviewService>();
        services.AddTransient<INodeQueryService, NodeQueryService>();
        services.AddTransient<ISearchService, SearchService>();
        services.AddTransient<IExclusionService, ExclusionService>();
        services.AddHttpClient<GitLabRepoProvider>();

        // Memory graph
        services.AddTransient<MemoryNormalizationService>();
        services.AddTransient<MemoryRetrievalService>();
        services.AddTransient<MemoryService>();

        // Messaging — IMessageBus wraps MassTransit IPublishEndpoint
        services.AddTransient<IMessageBus, MassTransitMessageBus>();

        // MassTransit + RabbitMQ
        var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
        services.AddMassTransit(x =>
        {
            x.AddConsumer<ProcessRepositoryConsumer>();
            x.AddConsumer<RepositoryIndexingCompletedConsumer>();
            x.AddConsumer<AnalysisBatchSubmittedConsumer>();
            x.AddConsumer<ProjectAnalysisResultsProcessedConsumer>();
            x.AddConsumer<AnalysisSynthesisCompletedConsumer>();
            x.AddConsumer<RepositoryRemovedConsumer>();
            x.AddConsumer<StoreMemoryConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(rabbitHost, "/", h =>
                {
                    h.Username(Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest");
                    h.Password(Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "guest");
                });

                var consumerOptions = appSettings.ConsumerOptions;

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
                    e.UseMessageRetry(retry => retry
                        .Incremental(3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))
                        .Ignore<BatchNotReadyException>());
                    e.UseDelayedRedelivery(redelivery => redelivery
                        .Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
                            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15))
                        .Handle<BatchNotReadyException>());
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

                cfg.ReceiveEndpoint("store-memory", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<StoreMemoryConsumer>(context);
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
        .WithTools<MemoryMcpServer>();
    }

    public static void Configure(WebApplication app, CodeGraphServiceSettings appSettings)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeGraph API v1"));
        app.UseCors();
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        app.MapMcp();

        // Seed exclusion rules from config on first run
        using var scope = app.Services.CreateScope();
        var exclusionService = scope.ServiceProvider.GetRequiredService<IExclusionService>();
        var gitLabOptions = scope.ServiceProvider.GetRequiredService<GitLabOptions>();
        Task.Run(() => exclusionService.SeedFromConfigAsync(gitLabOptions.ExcludedGroups)).GetAwaiter().GetResult();
    }
}
