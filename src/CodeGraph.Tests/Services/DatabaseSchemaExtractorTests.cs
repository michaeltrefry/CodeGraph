using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.DatabaseSchema;
using CodeGraph.Tests.Data;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DatabaseSchemaExtractorTests
{
    [Fact]
    public async Task SyncAllAsync_PropagatesFailuresAndDoesNotAdvanceLastSynced()
    {
        var sources = new RecordingDatabaseSourceStore(
        [
            new DatabaseSourceEntity
            {
                Id = 1,
                ServerName = "first",
                ConnectionString = "NotAConnectionString",
                Enabled = true
            },
            new DatabaseSourceEntity
            {
                Id = 2,
                ServerName = "second",
                ConnectionString = "AlsoNotAConnectionString",
                Enabled = true
            }
        ]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var extractor = new DatabaseSchemaExtractor(
            services.GetRequiredService<IServiceScopeFactory>(),
            sources,
            NullLogger<DatabaseSchemaExtractor>.Instance);

        var failure = await Should.ThrowAsync<AggregateException>(() => extractor.SyncAllAsync());

        failure.InnerExceptions.Count.ShouldBe(2);
        failure.Message.ShouldContain("2 of 2 enabled source(s)");
        sources.LastSyncedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task SyncAsync_DoesNotAdvanceLastSyncedWhenDiscoveryFails()
    {
        var source = new DatabaseSourceEntity
        {
            Id = 7,
            ServerName = "analytics",
            ConnectionString = "NotAConnectionString",
            Enabled = true
        };
        var sources = new RecordingDatabaseSourceStore([source]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var extractor = new DatabaseSchemaExtractor(
            services.GetRequiredService<IServiceScopeFactory>(),
            sources,
            NullLogger<DatabaseSchemaExtractor>.Instance);

        await Should.ThrowAsync<Exception>(() => extractor.SyncAsync(source));

        sources.LastSyncedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task SyncAsync_PreservesExistingGraphAndLastSyncedWhenAtomicReplacementFails()
    {
        var baseConnectionString = MariaDbTestEnvironment.RequireConnectionString();
        var databaseName = $"codegraph_schema_retry_{Guid.NewGuid():N}";
        var adminBuilder = new MySqlConnectionStringBuilder(baseConnectionString) { Database = "" };
        var sourceBuilder = new MySqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
        var projectName = $"db:integration:{databaseName}";

        await using (var admin = new MySqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await new MySqlCommand($"CREATE DATABASE `{databaseName}`", admin).ExecuteNonQueryAsync();
        }

        try
        {
            await using (var sourceConnection = new MySqlConnection(sourceBuilder.ConnectionString))
            {
                await sourceConnection.OpenAsync();
                await new MySqlCommand("CREATE TABLE orders (id BIGINT PRIMARY KEY)", sourceConnection)
                    .ExecuteNonQueryAsync();
            }

            var source = new DatabaseSourceEntity
            {
                Id = 9,
                ServerName = "integration",
                DatabaseName = databaseName,
                ConnectionString = sourceBuilder.ConnectionString,
                Enabled = true
            };
            var sources = new RecordingDatabaseSourceStore([source]);
            var graph = new InMemoryGraphStore();
            graph.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.Table,
                Name = "existing",
                QualifiedName = $"integration.{databaseName}.existing"
            });
            graph.ReplacementFailure = new InvalidOperationException("atomic replacement failed");
            using var services = new ServiceCollection()
                .AddSingleton<IGraphStore>(graph)
                .BuildServiceProvider();
            var extractor = new DatabaseSchemaExtractor(
                services.GetRequiredService<IServiceScopeFactory>(),
                sources,
                NullLogger<DatabaseSchemaExtractor>.Instance);

            var failure = await Should.ThrowAsync<InvalidOperationException>(() => extractor.SyncAsync(source));

            failure.Message.ShouldBe("atomic replacement failed");
            graph.Nodes.Single().Name.ShouldBe("existing");
            sources.LastSyncedIds.ShouldBeEmpty();
        }
        finally
        {
            await using var admin = new MySqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await new MySqlCommand($"DROP DATABASE IF EXISTS `{databaseName}`", admin).ExecuteNonQueryAsync();
        }
    }

    private sealed class RecordingDatabaseSourceStore(IReadOnlyList<DatabaseSourceEntity> sources)
        : IDatabaseSourceStore
    {
        public List<long> LastSyncedIds { get; } = [];

        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() => Task.FromResult(sources);
        public Task<DatabaseSourceEntity?> GetAsync(long id) =>
            Task.FromResult(sources.SingleOrDefault(source => source.Id == id));
        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateMcpExposureAsync(long id, bool? mcpHubEnabled, string? mcpExposureMode, string? mcpDisplayName, string? mcpEnvironment) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();

        public Task UpdateLastSyncedAsync(long id)
        {
            LastSyncedIds.Add(id);
            return Task.CompletedTask;
        }
    }
}
