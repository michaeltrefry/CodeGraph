using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Indexer.Client;
using CodeGraph.Host.Shared.Logging;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Services;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.WikiRag;
using Microsoft.Extensions.Options;

namespace CodeGraph.Jobs;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);
        services.AddHttpClient();
        services.AddCodeGraphIndexerClient(configuration);

        RegisterPersistence(services, configuration);
        services.AddSingleton<IFileSystem, LocalFileSystem>();
        services.AddTransient<IMcpDocService, McpDocService>();
        services.AddTransient<IAssistantRetentionCleanupService, AssistantRetentionCleanupService>();
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
        services.AddTransient<IMarkdownWikiChunker, MarkdownWikiChunker>();
        services.AddTransient<IConventionEmbeddingService, ConventionEmbeddingService>();
        services.AddCodeGraphJobScheduling();
        services.AddHostedService<ScheduleRunnerWorker>();
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
                options.MigrationLockTimeoutSeconds = storageOptions.MariaDbMigrationLockTimeoutSeconds;
                options.EncryptionKey = storageOptions.MariaDbEncryptionKey;
            });
            services.AddApplicationDatabaseLogging(configuration, ApplicationLogServices.Jobs);
            return;
        }

        services.AddSingleton<Neo4jSessionFactory>();
        services.AddTransient<IGraphStore, Neo4jGraphStore>();
        services.AddTransient<IMigrationRunner>(sp => sp.GetRequiredService<IGraphStore>());
        services.AddTransient<IJobScheduleStore, Neo4jJobScheduleStore>();
        services.AddTransient<IWikiStore, Neo4jWikiStore>();
        services.AddTransient<IVectorStore, Neo4jVectorStore>();
        services.AddTransient<IDbHealthStore>(sp => sp.GetRequiredService<IGraphStore>() as IDbHealthStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IDbHealthStore"));
        services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
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
