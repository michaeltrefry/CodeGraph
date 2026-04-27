using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Indexer.Client;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Services;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;

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
        services.AddCodeGraphJobScheduling();
        services.AddHostedService<ScheduleRunnerWorker>();
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
        services.AddTransient<IJobScheduleStore, Neo4jJobScheduleStore>();
        services.AddTransient<IWikiStore, Neo4jWikiStore>();
        services.AddTransient<IDbHealthStore>(sp => sp.GetRequiredService<IGraphStore>() as IDbHealthStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IDbHealthStore"));
        services.AddTransient<IExclusionStore>(sp => sp.GetRequiredService<IGraphStore>() as IExclusionStore
            ?? throw new InvalidOperationException("IGraphStore does not implement IExclusionStore"));
    }

    private static bool IsMariaDbProvider(CodeGraphStorageOptions storageOptions) =>
        storageOptions.Provider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase)
        || storageOptions.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);
}
