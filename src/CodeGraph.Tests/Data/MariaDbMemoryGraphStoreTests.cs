using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Memory;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbMemoryGraphStoreTests
{
    [Fact]
    public void MySqlMemoryGraphStore_ImplementsStandaloneMemoryContract()
    {
        typeof(IMemoryGraphStore).IsAssignableFrom(typeof(MySqlMemoryGraphStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlMemoryGraphStore_RoundTripsClaimCentricMemoryWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_memory_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });

        var runner = new MariaDbMigrationRunner(
            storageOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var store = new MySqlMemoryGraphStore(storageOptions);
            var receipt = new MemoryWriteReceipt
            {
                Id = "memory_write_test",
                Source = "test",
                EntitiesRequested = 2,
                ClaimsRequested = 1,
                EvidenceRequested = 1,
            };

            await store.CreateWriteReceiptAsync(receipt);
            (await store.GetWriteReceiptAsync(receipt.Id))!.Status.ShouldBe(MemoryWriteReceiptStatus.Queued);

            await store.UpsertEntitiesBatchAsync(
            [
                new MemoryEntity
                {
                    Id = "codegraph",
                    Label = "CodeGraph",
                    Type = "project",
                    Summary = "Indexes code",
                    Source = "test",
                    Embedding = [1f, 0f, 0f],
                },
                new MemoryEntity
                {
                    Id = "mariadb",
                    Label = "MariaDB",
                    Type = "database",
                    Summary = "Stores CodeGraph data",
                    Source = "test",
                    Embedding = [0.8f, 0.1f, 0f],
                }
            ]);

            var claim = new MemoryClaim
            {
                Id = "claim_codegraph_uses_mariadb",
                ClaimKey = "claim_key_uses_mariadb",
                FactGroupKey = "fact_group_storage",
                SubjectEntityId = "codegraph",
                Predicate = "uses",
                ObjectEntityId = "mariadb",
                NormalizedText = "codegraph uses mariadb",
                Status = MemoryClaimStatus.Active,
                Source = "test",
                Embedding = [1f, 0f, 0f],
            };

            await store.UpsertClaimsBatchAsync([claim]);
            await store.UpsertEntityEdgesBatchAsync(
            [
                new MemoryEntityEdge
                {
                    FromEntityId = "codegraph",
                    ToEntityId = "mariadb",
                    EdgeType = "uses",
                    BestActiveClaimId = claim.Id,
                }
            ]);
            await store.AddEvidenceBatchAsync(
            [
                new MemoryEvidence
                {
                    Id = "evidence_1",
                    ClaimId = claim.Id,
                    EvidenceType = "test",
                    SourceRef = "test",
                    Snippet = "verified",
                }
            ]);

            (await store.GetEntityAsync("codegraph"))!.Label.ShouldBe("CodeGraph");
            (await store.GetClaimAsync(claim.Id))!.ObjectEntityId.ShouldBe("mariadb");
            (await store.SearchClaimsAsync("uses mariadb", null, limit: 5)).ShouldContain(item => item.Claim.Id == claim.Id);
            (await store.GetRelationshipsAsync("codegraph")).Single().TargetId.ShouldBe("mariadb");
            (await store.GetEntityBundleAsync("codegraph"))!.ActiveClaims.Single().Id.ShouldBe(claim.Id);
            (await store.GetClaimBundleAsync(claim.Id))!.Evidence.Single().Id.ShouldBe("evidence_1");
            (await store.VectorSearchAsync([1f, 0f, 0f], topK: 1)).Single().Entity.Id.ShouldBe("codegraph");

            await store.UpdateWriteReceiptStatusAsync(receipt.Id, MemoryWriteReceiptStatus.Completed, new StoreMemoryResult
            {
                NodesWritten = 2,
                ClaimsWritten = 1,
                EvidenceWritten = 1,
            });
            (await store.GetWriteReceiptAsync(receipt.Id))!.ClaimsWritten.ShouldBe(1);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
