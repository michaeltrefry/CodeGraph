using System.Text.Json.Serialization;
using Anthropic;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Consumers;
using CodeGraph.Api.Memory;
using CodeGraph.Api.Middleware;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using CodeGraph.Api.Indexer;
using CodeGraph.Data;
using CodeGraph.Data.Migration;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Extractors.Ansible;
using CodeGraph.Extractors.ColdFusion;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Extractors.Sql;
using CodeGraph.Extractors.Terraform;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Indexer.Client;
using CodeGraph.Jobs;
using CodeGraph.Memory.Client;
using ModelContextProtocol.AspNetCore;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.DatabaseSchema;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Prompts;
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
        services.AddHttpContextAccessor();

        services
            .AddMvc()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
        {
            var authOptions = configuration
                .GetSection($"{CodeGraphOptionsServiceCollectionExtensions.SectionName}:{nameof(CodeGraphServiceSettings.AuthOptions)}")
                .Get<AuthOptions>() ?? new AuthOptions();
            var origins = new[]
                {
                    $"http://localhost:{Port}",
                    "http://localhost:4200"
                }
                .Concat(authOptions.AllowedOrigins)
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(origin => origin.Trim().TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }));

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

        RegisterAuthentication(services, configuration);

        RegisterPersistence(services, configuration);

        // Embeddings + semantic search
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
        services.AddTransient<ISemanticSearchService, SemanticSearchService>();
        services.AddTransient<GraphQueryEngine>();
        services.AddTransient<IndexingPipeline>();
        services.AddTransient<CrossRepoLinker>();
        services.AddTransient<GraphAssistant>();
        services.AddScoped<IAssistantDebugCapture, AssistantDebugCapture>();
        services.AddTransient<IAssistantConfigurationService, AssistantConfigurationService>();
        services.AddTransient<IAssistantRunService, AssistantRunService>();
        services.AddTransient<IAssistantRetentionCleanupService, AssistantRetentionCleanupService>();
        services.AddSingleton<IAssistantRunBackgroundRunner, AssistantRunBackgroundRunner>();
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
        services.AddTransient<ICodeExtractor, AnsibleExtractor>();
        services.AddTransient<ICodeExtractor, ColdFusionExtractor>();
        services.AddTransient<ICodeExtractor, TerraformExtractor>();
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
        services.AddScoped<IAnalysisModelProvider, AnthropicAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, OpenAiAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, GeminiAnalysisProvider>();
        services.AddScoped<IAnalysisModelProvider, LocalAnalysisProvider>();
        services.AddScoped<IAnalysisProviderRegistry, AnalysisProviderRegistry>();
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
        services.AddTransient<IAdminReportsService, AdminReportsService>();
        RegisterIndexerOperations(services, configuration);
        services.AddTransient<IAgentPromptService, AgentPromptService>();
        services.AddTransient<IWikiService, WikiService>();
        services.AddTransient<IWikiSectionSeedService, WikiSectionSeedService>();
        services.AddTransient<IAttachmentService, AttachmentService>();
        services.AddTransient<IMcpDocService, McpDocService>();
        services.AddTransient<McpPersonalAccessTokenService>();
        services.AddTransient<IMetricsEventPublisher, MetricsEventPublisher>();
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
        RegisterMemoryOperations(services, configuration);

        // Messaging — IMessageBus wraps MassTransit IPublishEndpoint
        services.AddTransient<IMessageBus, MassTransitMessageBus>();

        RegisterMessaging(services, configuration);
        RegisterMcp(services);
    }

    private static void RegisterMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var indexerOptions = configuration
            .GetSection(IndexerClientOptions.SectionPath)
            .Get<IndexerClientOptions>() ?? new IndexerClientOptions();
        var useRemoteIndexer = !string.IsNullOrWhiteSpace(indexerOptions.BaseUrl);
        var memoryOptions = configuration
            .GetSection(MemoryClientOptions.SectionPath)
            .Get<MemoryClientOptions>() ?? new MemoryClientOptions();
        var useRemoteMemory = !string.IsNullOrWhiteSpace(memoryOptions.BaseUrl);

        services.AddMassTransit(x =>
        {
            x.AddDelayedMessageScheduler();
            if (!useRemoteIndexer)
            {
                x.AddConsumer<ProcessRepositoryConsumer>();
                x.AddConsumer<RepositoryIndexingCompletedConsumer>();
                x.AddConsumer<AnalysisBatchSubmittedConsumer>();
                x.AddConsumer<ProjectAnalysisResultsProcessedConsumer>();
                x.AddConsumer<AnalysisSynthesisCompletedConsumer>();
                x.AddConsumer<RepositoryRemovedConsumer>();
            }

            if (!useRemoteMemory)
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
                if (!useRemoteIndexer)
                {
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
                }

                if (!useRemoteMemory)
                {
                    cfg.ReceiveEndpoint("store-memory-claims", e =>
                    {
                        ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                        e.ConfigureConsumer<StoreMemoryClaimsConsumer>(context);
                    });
                }
            });
        });
    }

    private static void RegisterMemoryOperations(IServiceCollection services, IConfiguration configuration)
    {
        var memoryOptions = configuration
            .GetSection(MemoryClientOptions.SectionPath)
            .Get<MemoryClientOptions>() ?? new MemoryClientOptions();

        if (!string.IsNullOrWhiteSpace(memoryOptions.BaseUrl))
        {
            services.AddCodeGraphMemoryClient(configuration);
            services.AddTransient<IMemoryOperationsService, RemoteMemoryOperationsService>();
            return;
        }

        services.AddTransient<IMemoryOperationsService, LocalMemoryOperationsService>();
    }

    private static void RegisterIndexerOperations(IServiceCollection services, IConfiguration configuration)
    {
        var indexerOptions = configuration
            .GetSection(IndexerClientOptions.SectionPath)
            .Get<IndexerClientOptions>() ?? new IndexerClientOptions();

        if (!string.IsNullOrWhiteSpace(indexerOptions.BaseUrl))
        {
            services.AddCodeGraphIndexerClient(configuration);
            services.AddTransient<IIndexerOperationsService, RemoteIndexerOperationsService>();
            return;
        }

        services.AddTransient<IDatabaseSchemaExtractor, DatabaseSchemaExtractor>();
        services.AddTransient<IndexerRunExecutor>();
        services.AddSingleton<IIndexerRunBackgroundRunner, IndexerRunBackgroundRunner>();
        services.AddTransient<IIndexerOperationsService, StandaloneIndexerOperationsService>();
    }

    private static void RegisterMcp(IServiceCollection services)
    {
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
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeGraph API v1"));
        }

        app.UseCors();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<McpTelemetryMiddleware>();
        app.MapControllers();

        var mcpEndpoint = app.MapMcp("/mcp");
        var mcpOptions = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;
        var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (mcpOptions.RequirePersonalAccessToken || authOptions.Enabled)
            mcpEndpoint.RequireAuthorization(McpPatAuthenticationDefaults.Policy);
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

    private static void RegisterAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var authOptions = configuration
            .GetSection($"{CodeGraphOptionsServiceCollectionExtensions.SectionName}:{nameof(CodeGraphServiceSettings.AuthOptions)}")
            .Get<AuthOptions>() ?? new AuthOptions();

        services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CodeGraphAuthenticationDefaults.Scheme;
                options.DefaultChallengeScheme = CodeGraphAuthenticationDefaults.Scheme;
            })
            .AddPolicyScheme(CodeGraphAuthenticationDefaults.Scheme, "CodeGraph auth selector", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var configured = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                    if (!configured.Enabled)
                        return CodeGraphAuthenticationDefaults.LocalDevScheme;

                    return CodeGraphAuthenticationDefaults.JwtBearerScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, LocalDevelopmentAuthenticationHandler>(
                CodeGraphAuthenticationDefaults.LocalDevScheme,
                _ => { })
            .AddJwtBearer(CodeGraphAuthenticationDefaults.JwtBearerScheme, options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
                options.Authority = string.IsNullOrWhiteSpace(authOptions.Authority) ? null : authOptions.Authority.TrimEnd('/');
                options.Audience = string.IsNullOrWhiteSpace(authOptions.Audience) ? null : authOptions.Audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",
                    ValidateAudience = HasConfiguredAudience(authOptions),
                    ValidAudience = string.IsNullOrWhiteSpace(authOptions.Audience) ? null : authOptions.Audience,
                    ValidAudiences = authOptions.ValidAudiences.Length > 0
                        ? authOptions.ValidAudiences.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                        : null
                };
            })
            .AddScheme<AuthenticationSchemeOptions, McpPatAuthenticationHandler>(
                McpPatAuthenticationDefaults.Scheme,
                _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CodeGraphAuthenticationDefaults.UserPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(CodeGraphAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(CodeGraphAuthenticationDefaults.AdminPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(CodeGraphAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AdminAuthorizationRequirement());
            });

            options.AddPolicy(McpPatAuthenticationDefaults.Policy, policy =>
            {
                policy.AuthenticationSchemes.Add(McpPatAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            });

            if (authOptions.Enabled)
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder(CodeGraphAuthenticationDefaults.Scheme)
                    .RequireAuthenticatedUser()
                    .Build();
            }
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
            services.AddSingleton<Neo4jSessionFactory>();
            services.AddTransient<INeo4jToMariaDbGraphExporter, Neo4jGraphStore>();
            return;
        }

        services.AddSingleton<Neo4jSessionFactory>();
        services.AddTransient<IGraphStore, Neo4jGraphStore>();
        services.AddTransient<INeo4jToMariaDbGraphExporter>(sp => (Neo4jGraphStore)sp.GetRequiredService<IGraphStore>());
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

    private static bool HasConfiguredAudience(AuthOptions authOptions) =>
        !string.IsNullOrWhiteSpace(authOptions.Audience)
        || authOptions.ValidAudiences.Any(value => !string.IsNullOrWhiteSpace(value));

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
