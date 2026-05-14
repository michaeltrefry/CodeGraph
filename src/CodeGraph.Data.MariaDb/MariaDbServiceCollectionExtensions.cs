using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace CodeGraph.Data.MariaDb;

public static class MariaDbServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphMariaDbData(
        this IServiceCollection services,
        Action<MariaDbStorageOptions> configureOptions)
    {
        services.Configure(configureOptions);

        services.AddDbContext<CodeGraphDbContext>((sp, options) =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<MariaDbStorageOptions>>().Value;
            if (string.IsNullOrWhiteSpace(storageOptions.ConnectionString))
            {
                throw new InvalidOperationException("MariaDB connection string is required.");
            }

            options.UseMySql(
                storageOptions.ConnectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb));
        });

        services.AddTransient<IMigrationRunner, MariaDbMigrationRunner>();
        services.AddTransient<ConnectionStringEncryptor>();
        services.AddTransient<IAesEncryptor>(sp => sp.GetRequiredService<ConnectionStringEncryptor>());
        services.AddTransient<IAnalysisStore, MySqlAnalysisStore>();
        services.AddTransient<IMetricsStore, MySqlMetricsStore>();
        services.AddTransient<IReviewStore, MySqlReviewStore>();
        services.AddTransient<IGraphStore, MySqlGraphStore>();
        services.AddTransient<IWikiStore, MySqlWikiStore>();
        services.AddTransient<IExclusionStore, MySqlExclusionStore>();
        services.AddTransient<IJobScheduleStore, MySqlJobScheduleStore>();
        services.AddTransient<IDbHealthStore, MySqlDbHealthStore>();
        services.AddTransient<IAdminStore, MySqlAdminStore>();
        services.AddTransient<IDatabaseSourceStore, MySqlDatabaseSourceStore>();
        services.AddTransient<ILlmConfigRepository, LlmConfigRepository>();
        services.AddTransient<IIndexerRunStore, MySqlIndexerRunStore>();
        services.AddTransient<IVectorStore, MySqlVectorStore>();
        services.AddTransient<IMemoryGraphStore, MySqlMemoryGraphStore>();
        services.AddTransient<IAssistantRunStore, MySqlAssistantRunStore>();
        services.AddTransient<IMcpPersonalAccessTokenStore, MySqlMcpPersonalAccessTokenStore>();
        services.AddTransient<IMcpHubStore, MySqlMcpHubStore>();
        services.AddTransient<IMcpSensitiveColumnStore, MySqlMcpSensitiveColumnStore>();
        services.AddTransient<IMcpProviderCredentialStore, MySqlMcpProviderCredentialStore>();
        services.AddTransient<IMetricsEventStore, MySqlMetricsEventStore>();
        services.AddTransient<IAdminReportsStore, MySqlAdminReportsStore>();

        return services;
    }
}
