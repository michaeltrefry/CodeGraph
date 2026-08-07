using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Memory;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
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
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

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

            var tenantContext = new MemoryTenantContext();
            using var tenantScope = tenantContext.Enter(MemoryTenantContext.ForAuthenticatedUser("integration-user"));
            var store = new MySqlMemoryGraphStore(storageOptions, tenantContext);
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
            await store.CreateObservationAsync(new MemoryObservation
            {
                Id = "observation_1",
                Claim = claim.Id,
                ConflictsWith = claim.Id,
                Source = "test",
                AboutEntityIds = ["codegraph"],
                AboutClaimIds = [claim.Id],
            });

            (await store.GetEntityAsync("codegraph"))!.Label.ShouldBe("CodeGraph");
            (await store.GetClaimAsync(claim.Id))!.ObjectEntityId.ShouldBe("mariadb");
            (await store.SearchClaimsAsync("uses mariadb", null, limit: 5)).ShouldContain(item => item.Claim.Id == claim.Id);
            (await store.GetRelationshipsAsync("codegraph")).Single().TargetId.ShouldBe("mariadb");
            (await store.GetEntityBundleAsync("codegraph"))!.ActiveClaims.Single().Id.ShouldBe(claim.Id);
            (await store.GetClaimBundleAsync(claim.Id))!.Evidence.Single().Id.ShouldBe("evidence_1");
            (await store.GetUnresolvedObservationsAsync(["codegraph"], [claim.Id]))
                .Single().Id.ShouldBe("observation_1");
            (await store.VectorSearchAsync([1f, 0f, 0f], topK: 1)).Single().Entity.Id.ShouldBe("codegraph");

            await store.UpdateWriteReceiptStatusAsync(receipt.Id, MemoryWriteReceiptStatus.Completed, new StoreMemoryResult
            {
                NodesWritten = 2,
                ClaimsWritten = 1,
                EvidenceWritten = 1,
            });
            (await store.GetWriteReceiptAsync(receipt.Id))!.ClaimsWritten.ShouldBe(1);

            using (tenantContext.Enter(MemoryTenantContext.ForAuthenticatedUser("second-user")))
            {
                (await store.GetEntityAsync("codegraph")).ShouldBeNull();
                (await store.GetClaimAsync(claim.Id)).ShouldBeNull();
                (await store.GetWriteReceiptAsync(receipt.Id)).ShouldBeNull();
                (await store.TextSearchAsync("CodeGraph", 5)).ShouldBeEmpty();
                (await store.SearchClaimsAsync("uses mariadb", null, 5)).ShouldBeEmpty();
                (await store.GetUnresolvedObservationsAsync(["codegraph"], [claim.Id])).ShouldBeEmpty();

                await store.UpsertEntitiesBatchAsync(
                [
                    new MemoryEntity
                    {
                        Id = "codegraph",
                        Label = "Second User CodeGraph",
                        Type = "project",
                        Summary = "Private second-user memory",
                        Source = "test",
                    }
                ]);
                await store.CreateWriteReceiptAsync(new MemoryWriteReceipt
                {
                    Id = "memory_write_second_user",
                    Source = "test",
                    EntitiesRequested = 1,
                });
                var secondUserClaim = new MemoryClaim
                {
                    Id = claim.Id,
                    ClaimKey = claim.ClaimKey,
                    FactGroupKey = claim.FactGroupKey,
                    SubjectEntityId = "codegraph",
                    Predicate = "owns",
                    ValueText = "private memory",
                    NormalizedText = "second user owns private memory",
                    Status = MemoryClaimStatus.Active,
                    Source = "test",
                };
                await store.UpsertClaimsBatchAsync([secondUserClaim]);
                await store.CreateObservationAsync(new MemoryObservation
                {
                    Id = "observation_1",
                    Claim = secondUserClaim.Id,
                    ConflictsWith = secondUserClaim.Id,
                    Source = "test",
                    AboutEntityIds = ["codegraph"],
                    AboutClaimIds = [secondUserClaim.Id],
                });
                await store.AddEvidenceBatchAsync(
                [
                    new MemoryEvidence
                    {
                        Id = "evidence_1",
                        ClaimId = secondUserClaim.Id,
                        EvidenceType = "test",
                        SourceRef = "second-user-test",
                    }
                ]);

                (await store.GetEntityAsync("codegraph"))!.Label.ShouldBe("Second User CodeGraph");
                (await store.GetClaimAsync(claim.Id))!.Predicate.ShouldBe("owns");
                (await store.GetClaimBundleAsync(claim.Id))!.Evidence.Single().SourceRef.ShouldBe("second-user-test");
                (await store.GetUnresolvedObservationsAsync(["codegraph"], [claim.Id]))
                    .Single().Id.ShouldBe("observation_1");
                (await store.GetWriteReceiptAsync("memory_write_second_user")).ShouldNotBeNull();

                var cleanup = await store.DeleteMemoryBySourceAsync("test", dryRun: false);
                cleanup.Username.ShouldBe(MemoryTenantContext.ForAuthenticatedUser("second-user"));
                cleanup.EntitiesDeleted.ShouldBe(1);
                (await store.GetEntityAsync("codegraph")).ShouldBeNull();
                (await store.GetWriteReceiptAsync("memory_write_second_user")).ShouldBeNull();
            }

            (await store.GetEntityAsync("codegraph"))!.Label.ShouldBe("CodeGraph");
            (await store.GetClaimAsync(claim.Id)).ShouldNotBeNull();
            (await store.GetClaimBundleAsync(claim.Id))!.Evidence.Single().Id.ShouldBe("evidence_1");
            (await store.GetUnresolvedObservationsAsync(["codegraph"], [claim.Id]))
                .Single().Id.ShouldBe("observation_1");
            (await store.GetWriteReceiptAsync(receipt.Id)).ShouldNotBeNull();

            await using (var connection = new MySqlConnection(builder.ConnectionString))
            {
                var policy = await connection.QuerySingleAsync<(string Status, string? Owner)>(
                    "SELECT ownership_status AS Status, owner_username AS Owner FROM memory_tenant_ownership WHERE username = 'default';");
                policy.Status.ShouldBe("quarantined");
                policy.Owner.ShouldBeNull();
            }

            await using (var db = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var auditStore = new MySqlMemoryAdminAuditStore(db);
                var auditId = await auditStore.CreatePendingAsync(new MemoryAdminAuditEntity
                {
                    ActorUsername = "user:admin",
                    TargetUsername = "default",
                    Operation = "diagnostics",
                    DryRun = true,
                });
                await auditStore.SetOutcomeAsync(auditId, "completed", true, null);

                var audit = await db.MemoryAdminAudit.SingleAsync();
                audit.ActorUsername.ShouldBe("user:admin");
                audit.TargetUsername.ShouldBe("default");
                audit.OutcomeStatus.ShouldBe("completed");
                audit.Succeeded.ShouldBe(true);
                audit.CompletedAt.ShouldNotBeNull();
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CodeGraphDbContext>();
        options.UseMySql(
            connectionString,
            ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb));
        return options.Options;
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
