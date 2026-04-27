using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCodeGraphMariaDbData_RegistersStandaloneStoreContracts()
    {
        var services = new ServiceCollection();

        services.AddCodeGraphMariaDbData(options =>
        {
            options.ConnectionString = "Server=localhost;Database=codegraph;User ID=root;Password=test";
        });

        var descriptors = services.ToList();

        descriptors.ShouldContain(d => d.ServiceType == typeof(IGraphStore) && d.ImplementationType == typeof(MySqlGraphStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IVectorStore) && d.ImplementationType == typeof(MySqlVectorStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IWikiStore) && d.ImplementationType == typeof(MySqlWikiStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IMigrationRunner) && d.ImplementationType == typeof(MariaDbMigrationRunner));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IJobScheduleStore) && d.ImplementationType == typeof(MySqlJobScheduleStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IDbHealthStore) && d.ImplementationType == typeof(MySqlDbHealthStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IMemoryGraphStore) && d.ImplementationType == typeof(MySqlMemoryGraphStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IAssistantRunStore) && d.ImplementationType == typeof(MySqlAssistantRunStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IMcpPersonalAccessTokenStore) && d.ImplementationType == typeof(MySqlMcpPersonalAccessTokenStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IMetricsEventStore) && d.ImplementationType == typeof(MySqlMetricsEventStore));
        descriptors.ShouldContain(d => d.ServiceType == typeof(ILlmConfigRepository) && d.ImplementationType == typeof(LlmConfigRepository));
        descriptors.ShouldContain(d => d.ServiceType == typeof(IAesEncryptor));
    }
}
