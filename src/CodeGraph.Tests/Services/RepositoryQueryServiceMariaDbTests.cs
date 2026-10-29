using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class RepositoryQueryServiceMariaDbTests
{
    [Fact]
    public async Task SchemaQueries_CompleteAgainstOneSharedDbContext()
    {
        var rootConnectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(rootConnectionString);
        var databaseName = $"codegraph_repository_query_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(
            storageOptions,
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(dbOptions);
            var store = new MySqlGraphStore(
                context,
                storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context),
                new MySqlMetricsStore(context),
                new MySqlReviewStore(context),
                runner);
            var service = new RepositoryQueryService(
                store,
                Options.Create(new RepositorySourceOptions()));
            const string repository = "db:sql-prod/Orders";

            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = repository,
                SourceGroup = "sql-prod",
                Language = "SQL"
            });
            await store.UpsertNodeAsync(new GraphNode
            {
                Repository = repository,
                Label = NodeLabel.Table,
                Name = "Orders",
                QualifiedName = "dbo.Orders"
            });

            var nodes = await service.GetNodesAsync(repository, null, null, 1, 50);
            var catalog = await service.GetSchemaCatalogAsync(repository);

            nodes.Total.ShouldBe(1);
            nodes.Items.Single().Name.ShouldBe("Orders");
            catalog.ShouldNotBeNull();
            catalog.Tables.Single().Name.ShouldBe("Orders");
        }
        finally
        {
            builder.Database = "";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
        }
    }
}
