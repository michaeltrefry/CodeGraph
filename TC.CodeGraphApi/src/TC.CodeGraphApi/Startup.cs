using System.Text.Json.Serialization;
using Anthropic;
using Autofac;
using MassTransit.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TC.CodeGraphApi.Auth;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Data.Neo4j;
using TC.CodeGraphApi.Extractors.ColdFusion;
using TC.CodeGraphApi.Extractors.CSharp;
using TC.CodeGraphApi.Extractors.Sql;
using TC.CodeGraphApi.Extractors.Ansible;
using TC.CodeGraphApi.Extractors.Terraform;
using TC.CodeGraphApi.Extractors.TypeScript;
using ModelContextProtocol.AspNetCore;
using TC.CodeGraphApi.Consumers;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Services.Analyzers;
using TC.CodeGraphApi.Services.Configuration;
using TC.CodeGraphApi.Services.Embeddings;
using TC.CodeGraphApi.Services.Assistant;
using TC.CodeGraphApi.Services.Memory;
using TC.Common.Configuration;
using TC.Common.TcServiceStack;
using TC.Common.TcServiceStack.DependencyInjection.AutoFac;
using TC.Jarvis.ApiDocumentation.TcApi;
using TC.Jarvis.DependencyInjection;
using DiBuilder = TC.Common.TcServiceStack.DependencyInjection.DiBuilder;

namespace TC.CodeGraphApi;

public class Startup(IWebHostEnvironment env)
    : TcServiceStartup<CodeGraphServiceSettings>(nameof(CodeGraphApi), Port, env),
        IServiceProviderFactory<IServiceCollection>
{

    public new const int Port = 5037;
    
    protected override DiBuilder BuildContainer(ContainerBuilder autofac)
    {
        var builder = DiBuilder
            .Init(Using.Autofac(autofac))
            .WithRegistrations(container =>
            {
                container.RegisterSettings<CodeGraphServiceSettings>();

                if (AppSettings.StorageOptions.IsNeo4j)
                {
                    container.Register(_ => new Neo4jSessionFactory(AppSettings.StorageOptions))
                        .Scoped(Scope.SingleInstance);
                    container.RegisterType<Neo4jGraphStore>().As<IGraphStore>().As<IExclusionStore>().Scoped(Scope.Transient);
                    container.RegisterType<Neo4jVectorStore>().As<IVectorStore>().Scoped(Scope.Transient);
                    container.RegisterType<Neo4jWikiStore>().As<IWikiStore>().Scoped(Scope.Transient);
                    container.RegisterType<Neo4jAdminStore>().As<IAdminStore>().Scoped(Scope.Transient);
                    container.RegisterType<Neo4jMemoryGraphStore>().As<IMemoryGraphStore>().Scoped(Scope.Transient);
                }
                else
                {
                    container.RegisterType<MySqlGraphStore>().As<IGraphStore>().As<IExclusionStore>().Scoped(Scope.Transient);
                    container.RegisterType<NullVectorStore>().As<IVectorStore>().Scoped(Scope.SingleInstance);
                    container.RegisterType<MySqlWikiStore>().As<IWikiStore>().Scoped(Scope.Transient);
                    container.RegisterType<MySqlAdminStore>().As<IAdminStore>().Scoped(Scope.Transient);
                }

                // Embedding service (ONNX) + semantic search
                container.RegisterType<OnnxEmbeddingService>().As<IEmbeddingService>().Scoped(Scope.SingleInstance);
                container.RegisterType<SemanticSearchService>().As<ISemanticSearchService>().Scoped(Scope.Transient);
                container.RegisterType<GraphQueryEngine>().Scoped(Scope.Transient);
                container.RegisterType<IndexingPipeline>().Scoped(Scope.Transient);
                container.RegisterType<CrossRepoLinker>().Scoped(Scope.Transient);
                container.RegisterType<GraphAssistant>().Scoped(Scope.Transient);
                container.RegisterType<CodeGraphDocGenerator>().Scoped(Scope.Transient);

                // Solution-level Roslyn analysis (populates DotnetProject per .csproj)
                container.RegisterType<SolutionAnalyzer>().As<ISolutionAnalyzer>().Scoped(Scope.Transient);

                // TypeScript/Angular analysis (Node.js sidecar — degrades gracefully if unavailable)
                container.Register(ctx =>
                {
                    var settings = ctx.GetInstance<ITcConfiguration<CodeGraphServiceSettings>>().Current;
                    var logger = ctx.GetInstance<ILoggerFactory>().CreateLogger<TypeScriptServerManager>();
                    return new TypeScriptServerManager(port: settings.TsPort, logger);
                }).Scoped(Scope.Transient);
                container.RegisterType<TypeScriptProjectAnalyzer>().As<ITypeScriptAnalyzer>().Scoped(Scope.Transient);

                // Lint / Trust scoring: Roslyn cache (C#) + ESLint sidecar (TS/JS)
                container.RegisterType<LintResultCache>().Scoped(Scope.SingleInstance);
                container.Register<ILintRunner>(ctx => new CompositeLintRunner(
                    ctx.GetInstance<LintResultCache>(),
                    ctx.GetInstance<TypeScriptServerManager>(),
                    ctx.GetInstance<ILoggerFactory>().CreateLogger<CompositeLintRunner>()
                )).Scoped(Scope.Transient);

                // NuGet reference extraction from .csproj files
                container.RegisterType<NuGetReferenceExtractor>().As<INuGetReferenceExtractor>().Scoped(Scope.Transient);

                // Extractors registered as a collection
                container.RegisterType<RoslynExtractor>().As<ICodeExtractor>();
                container.RegisterType<SqlExtractor>().As<ICodeExtractor>();
                container.RegisterType<ColdFusionExtractor>().As<ICodeExtractor>();
                container.RegisterType<TypeScriptExtractor>().As<ICodeExtractor>();
                container.RegisterType<AnsibleExtractor>().As<ICodeExtractor>();
                container.RegisterType<TerraformExtractor>().As<ICodeExtractor>();

                // File system abstraction (shared by analyzers, pipeline, query services)
                container.RegisterType<LocalFileSystem>().As<IFileSystem>().Scoped(Scope.SingleInstance);

                // Vitals (codebase health metrics)
                container.RegisterType<VitalsAnalyzer>().As<IVitalsAnalyzer>().Scoped(Scope.Transient);
                container.RegisterType<FileSystemSourceFileProvider>().As<ISourceFileProvider>().Scoped(Scope.SingleInstance);
                container.RegisterType<SecurityAnalyzer>().As<ISecurityAnalyzer>().Scoped(Scope.Transient);
                container.RegisterType<CommunityDetectionService>().As<ICommunityDetectionService>().Scoped(Scope.Transient);
                container.RegisterType<ImpactAnalysisService>().As<IImpactAnalysisService>().Scoped(Scope.Transient);

                // Claude analyzer + batch analysis
                container.Register(_ => new AnthropicClient()).Scoped(Scope.SingleInstance);
                container.RegisterType<AnthropicCircuitBreaker>().Scoped(Scope.SingleInstance);
                container.RegisterType<ClaudeCodeAnalyzer>().As<ICodeAnalyzer>().Scoped(Scope.Transient);
                container.RegisterType<BatchAnalysisService>().As<IBatchAnalysisService>().Scoped(Scope.Transient);
                container.RegisterType<GitLabRepoProvider>().As<IRepoProvider>().Scoped(Scope.Transient);
                container.RegisterType<ProjectService>().As<IProjectService>().Scoped(Scope.Transient);
                container.RegisterType<ProjectQueryService>().As<IProjectQueryService>().Scoped(Scope.Transient);
                container.RegisterType<AdminService>().As<IAdminService>().Scoped(Scope.Transient);
                container.RegisterType<AdminUserService>().As<IAdminUserService>().Scoped(Scope.Transient);
                container.RegisterType<SettingsService>().As<ISettingsService>().Scoped(Scope.Transient);
                container.RegisterType<WikiService>().As<IWikiService>().Scoped(Scope.Transient);
                container.RegisterType<AttachmentService>().As<IAttachmentService>().Scoped(Scope.Transient);
                container.RegisterType<McpDocService>().As<IMcpDocService>().Scoped(Scope.Transient);
                container.RegisterType<GraphOverviewService>().As<IGraphOverviewService>().Scoped(Scope.Transient);
                container.RegisterType<NodeQueryService>().As<INodeQueryService>().Scoped(Scope.Transient);
                container.RegisterType<SearchService>().As<ISearchService>().Scoped(Scope.Transient);
                container.RegisterType<ExclusionService>().As<IExclusionService>().Scoped(Scope.Transient);

                // Memory graph services
                container.RegisterType<MemoryNormalizationService>().Scoped(Scope.Transient);
                container.RegisterType<MemoryRetrievalService>().Scoped(Scope.Transient);
                container.RegisterType<MemoryService>().Scoped(Scope.Transient);

                container.AddTcQueueing(builder =>
                {
                    builder.RegisterEnterpriseBus()
                        .AddConsumer<ProcessRepositoryConsumer,
                            TcConsumerDefinition<ProcessRepositoryConsumer>>()
                        .AddConsumer<RepositoryIndexingCompletedConsumer,
                            TcConsumerDefinition<RepositoryIndexingCompletedConsumer>>()
                        .AddConsumer<AnalysisBatchSubmittedConsumer,
                            TcConsumerDefinition<AnalysisBatchSubmittedConsumer>>()
                        .AddConsumer<ProjectAnalysisResultsProcessedConsumer,
                            TcConsumerDefinition<ProjectAnalysisResultsProcessedConsumer>>()
                        .AddConsumer<AnalysisSynthesisCompletedConsumer,
                            TcConsumerDefinition<AnalysisSynthesisCompletedConsumer>>()
                        // ConventionUpdatedConsumer removed — no wiki event needed
                        .AddConsumer<RepositoryRemovedConsumer,
                            TcConsumerDefinition<RepositoryRemovedConsumer>>()
                        .AddConsumer<StoreMemoryConsumer,
                            TcConsumerDefinition<StoreMemoryConsumer>>();
                });
            });

        return builder;
    }
    
    public void Configure(IApplicationBuilder app)
    {
        app.UseCors();
        app.UseTcApiDocumentationUi();
        DefaultConfigure(app);

        // Seed exclusion rules from config on first run
        // Use Task.Run to avoid synchronization context deadlock in sync Configure
        using var scope = app.ApplicationServices.CreateScope();
        var exclusionService = scope.ServiceProvider.GetRequiredService<IExclusionService>();
        var gitLabOptions = scope.ServiceProvider.GetRequiredService<GitLabOptions>();
        Task.Run(() => exclusionService.SeedFromConfigAsync(gitLabOptions.ExcludedGroups)).GetAwaiter().GetResult();
    }
    
    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        return services;
    }

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        AppSettings.LoadEnvironmentOverrides();

        // Auth is handled by TcServiceStartup via Consul SecuritySettings
        // (AuthorizationType = Api, Audience = codegraph-api)
        // Only register the custom Admin policy here.
        services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("Admin", p => p.AddRequirements(new AdminRequirement()));
        });
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
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}", "http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();
        
        services.AddSingleton<CodeGraphStorageOptions>(AppSettings.StorageOptions);
        services.AddSingleton<AnalysisOptions>(AppSettings.AnalysisOptions);
        services.AddSingleton<GitLabOptions>(AppSettings.GitLabOptions);
        services.AddSingleton<IndexingOptions>(AppSettings.IndexingOptions);
        services.AddSingleton<WikiOptions>(AppSettings.WikiOptions);
        services.AddSingleton<AuthOptions>(AppSettings.AuthOptions);
        services.AddHttpClient<GitLabRepoProvider>();

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

        return ConfigureDefaultContainer(services);
    }
}
