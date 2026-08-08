using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbIndexerRunStoreTests
{
    [Fact]
    public void MySqlIndexerRunStore_ImplementsStandaloneIndexerRunContract()
    {
        typeof(IIndexerRunStore).IsAssignableFrom(typeof(MySqlIndexerRunStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlIndexerRunStore_RoundTripsRunStatusWhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_indexer_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var store = new MySqlIndexerRunStore(context);

            var runId = await store.CreateIndexerRunAsync(new IndexerRunEntity
            {
                Operation = "index",
                RequestedByUsername = "codex",
                Target = "CodeGraph",
                Status = "queued"
            });

            runId.ShouldBeGreaterThan(0);
            (await store.GetIndexerRunAsync(runId))!.Status.ShouldBe("queued");

            await store.UpdateIndexerRunStatusAsync(runId, "running", message: "started");
            var running = await store.GetIndexerRunAsync(runId);
            running.ShouldNotBeNull();
            running.Status.ShouldBe("running");
            running.Message.ShouldBe("started");
            running.StartedAt.ShouldNotBeNull();

            await store.UpdateIndexerRunStatusAsync(runId, "completed", completedAt: DateTime.UtcNow);
            var completed = await store.GetIndexerRunAsync(runId);
            completed.ShouldNotBeNull();
            completed.Status.ShouldBe("completed");
            completed.CompletedAt.ShouldNotBeNull();

            var recent = await store.ListIndexerRunsAsync(status: "completed", operation: "index", take: 5);
            recent.Count.ShouldBe(1);
            recent[0].Id.ShouldBe(runId);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task DurableClaims_AreExclusiveFencedRecoverableAndCancellationAware_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_indexer_durable_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            long queuedId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                queuedId = await new MySqlIndexerRunStore(seedContext).CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "queued",
                    RetrySafe = true
                });
            }

            await using var firstContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            await using var secondContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var now = DateTime.UtcNow;
            var claims = await Task.WhenAll(
                new MySqlIndexerRunStore(firstContext).TryClaimNextIndexerRunAsync("worker-a", now, now.AddMinutes(1)),
                new MySqlIndexerRunStore(secondContext).TryClaimNextIndexerRunAsync("worker-b", now, now.AddMinutes(1)));
            claims.Count(claim => claim is not null).ShouldBe(1);
            var firstLease = claims.Single(claim => claim is not null)!;
            firstLease.Run.Id.ShouldBe(queuedId);
            firstLease.Run.AttemptCount.ShouldBe(1);

            await using (var cancellationContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var cancellationStore = new MySqlIndexerRunStore(cancellationContext);
                (await cancellationStore.RequestIndexerRunCancellationAsync(queuedId, now.AddSeconds(1)))!
                    .CancelRequestedAt.ShouldNotBeNull();
            }
            await using (var renewalContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var renewal = await new MySqlIndexerRunStore(renewalContext).RenewIndexerRunLeaseAsync(
                    queuedId,
                    firstLease.Owner,
                    firstLease.FencingToken,
                    now.AddSeconds(2),
                    now.AddMinutes(1));
                renewal.ShouldBe(new IndexerRunLeaseRenewal(true, true));
            }

            await using (var cancelContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                (await new MySqlIndexerRunStore(cancelContext).CancelOwnedIndexerRunAsync(
                    firstLease,
                    "Canceled by test.",
                    now.AddSeconds(3))).ShouldBeTrue();
            }

            long queuedCancellationId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var seedStore = new MySqlIndexerRunStore(seedContext);
                queuedCancellationId = await seedStore.CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "reindex_all",
                    Status = "queued"
                });
                var canceled = await seedStore.RequestIndexerRunCancellationAsync(queuedCancellationId, now);
                canceled!.Status.ShouldBe("canceled");
                canceled.CompletedAt.ShouldNotBeNull();
                canceled.Message.ShouldNotBeNull();
                canceled.Message.ShouldContain("before execution");
            }

            long retryId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                retryId = await new MySqlIndexerRunStore(seedContext).CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "running",
                    RetrySafe = true,
                    ExecutionOwner = "dead-worker",
                    LeaseExpiresAt = now.AddSeconds(-1),
                    HeartbeatAt = now.AddMinutes(-1),
                    AttemptCount = 1,
                    FencingToken = 7,
                    StartedAt = now.AddMinutes(-1)
                });
            }
            IndexerRunLease recovered;
            await using (var recoveryContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                recovered = (await new MySqlIndexerRunStore(recoveryContext).TryClaimNextIndexerRunAsync(
                    "recovery-worker",
                    now,
                    now.AddMinutes(1)))!;
                recovered.Run.Id.ShouldBe(retryId);
                recovered.Run.AttemptCount.ShouldBe(2);
                recovered.FencingToken.ShouldBe(8);
            }
            await using (var fenceContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var fenceStore = new MySqlIndexerRunStore(fenceContext);
                (await fenceStore.CompleteIndexerRunAsync(
                    new IndexerRunLease(recovered.Run, "dead-worker", 7),
                    "stale",
                    now)).ShouldBeFalse();
                (await fenceStore.FailOrRetryIndexerRunAsync(
                    recovered,
                    "transient_failure",
                    "transient",
                    now,
                    now.AddSeconds(30),
                    maxAttempts: 3)).ShouldBe(IndexerRunFailureDisposition.Retrying);
                var retry = await fenceStore.GetIndexerRunAsync(retryId);
                retry!.Status.ShouldBe("queued");
                retry.ErrorCode.ShouldBe("transient_failure");
                retry.NextAttemptAt.ShouldNotBeNull();
                retry.AttemptCount.ShouldBe(2);
            }

            long unsafeId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                unsafeId = await new MySqlIndexerRunStore(seedContext).CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "reindex_all",
                    Status = "running",
                    RetrySafe = false,
                    ExecutionOwner = "dead-publisher",
                    LeaseExpiresAt = now.AddSeconds(-1),
                    AttemptCount = 1,
                    FencingToken = 2
                });
            }
            await using (var unsafeContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var unsafeStore = new MySqlIndexerRunStore(unsafeContext);
                (await unsafeStore.TryClaimNextIndexerRunAsync("other-worker", now, now.AddMinutes(1))).ShouldBeNull();
                var failed = await unsafeStore.GetIndexerRunAsync(unsafeId);
                failed!.Status.ShouldBe("failed");
                failed.Error.ShouldNotBeNull();
                failed.Error.ShouldContain("lease expired", Case.Insensitive);
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ExpiredLease_CannotBeRenewedOrResurrectItsOwnership_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_late_renewal_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var claimedAt = DateTime.UtcNow;
            IndexerRunLease originalLease;
            await using (var claimContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlIndexerRunStore(claimContext);
                await store.CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "queued",
                    RetrySafe = true
                });
                originalLease = (await store.TryClaimNextIndexerRunAsync(
                    "paused-worker",
                    claimedAt,
                    claimedAt.AddSeconds(10)))!;
            }

            var afterExpiry = claimedAt.AddSeconds(11);
            await using (var lateRenewalContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var lateRenewal = await new MySqlIndexerRunStore(lateRenewalContext).RenewIndexerRunLeaseAsync(
                    originalLease.Run.Id,
                    originalLease.Owner,
                    originalLease.FencingToken,
                    afterExpiry,
                    afterExpiry.AddMinutes(1));
                lateRenewal.ShouldBe(new IndexerRunLeaseRenewal(false, false));
            }

            await using (var recoveryContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var recovered = await new MySqlIndexerRunStore(recoveryContext).TryClaimNextIndexerRunAsync(
                    "recovery-worker",
                    afterExpiry,
                    afterExpiry.AddMinutes(1));
                recovered.ShouldNotBeNull();
                recovered.Run.Id.ShouldBe(originalLease.Run.Id);
                recovered.Owner.ShouldBe("recovery-worker");
                recovered.FencingToken.ShouldBe(originalLease.FencingToken + 1);
                recovered.Run.AttemptCount.ShouldBe(2);
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ExpiredCancellationAndRetryBudget_AreTerminalAndNeverReplayed_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_recovery_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var now = DateTime.UtcNow;
            long canceledId;
            long exhaustedId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlIndexerRunStore(seedContext);
                canceledId = await store.CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "running",
                    RetrySafe = true,
                    ExecutionOwner = "gone",
                    LeaseExpiresAt = now.AddSeconds(-1),
                    CancelRequestedAt = now.AddSeconds(-2),
                    AttemptCount = 1,
                    FencingToken = 4
                });
                exhaustedId = await store.CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "running",
                    RetrySafe = true,
                    ExecutionOwner = "gone",
                    LeaseExpiresAt = now.AddSeconds(-1),
                    AttemptCount = 3,
                    FencingToken = 8
                });
            }

            await using (var recoveryContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlIndexerRunStore(recoveryContext);
                (await store.TryClaimNextIndexerRunAsync(
                    "recovery",
                    now,
                    now.AddMinutes(1),
                    maxAttempts: 3)).ShouldBeNull();
            }

            await using (var verifyContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlIndexerRunStore(verifyContext);
                var canceled = (await store.GetIndexerRunAsync(canceledId))!;
                canceled.Status.ShouldBe("canceled");
                canceled.CancelRequestedAt.ShouldNotBeNull();
                canceled.AttemptCount.ShouldBe(1);
                canceled.ExecutionOwner.ShouldBeNull();

                var exhausted = (await store.GetIndexerRunAsync(exhaustedId))!;
                exhausted.Status.ShouldBe("failed");
                exhausted.AttemptCount.ShouldBe(3);
                exhausted.Error.ShouldNotBeNull();
                exhausted.Error.ShouldContain("retry budget", Case.Insensitive);
                exhausted.ExecutionOwner.ShouldBeNull();
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task LoweredRetryBudget_DoesNotClaimAnAlreadyQueuedExhaustedRun_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_lowered_budget_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var now = DateTime.UtcNow;
            long runId;
            await using (var seedContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                runId = await new MySqlIndexerRunStore(seedContext).CreateIndexerRunAsync(new IndexerRunEntity
                {
                    Operation = "sync_schema",
                    Status = "queued",
                    RetrySafe = true,
                    AttemptCount = 3,
                    NextAttemptAt = now.AddSeconds(-1)
                });
            }

            await using (var claimContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var claimed = await new MySqlIndexerRunStore(claimContext).TryClaimNextIndexerRunAsync(
                    "worker-after-config-change",
                    now,
                    now.AddMinutes(1),
                    maxAttempts: 3);
                claimed.ShouldBeNull();
            }

            await using (var verifyContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var persisted = await new MySqlIndexerRunStore(verifyContext).GetIndexerRunAsync(runId);
                persisted!.Status.ShouldBe("failed");
                persisted.AttemptCount.ShouldBe(3);
                persisted.Error.ShouldContain("retry budget", Case.Insensitive);
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task DurableSubmissionIdentity_DeduplicatesConcurrentRequests_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_submit_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await using var firstContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            await using var secondContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var submissions = await Task.WhenAll(
                new MySqlIndexerRunStore(firstContext).CreateOrGetIndexerRunAsync(NewSubmission()),
                new MySqlIndexerRunStore(secondContext).CreateOrGetIndexerRunAsync(NewSubmission()));

            submissions.Select(result => result.RunId).Distinct().Count().ShouldBe(1);
            submissions.Count(result => result.Created).ShouldBe(1);

            await using var verifyContext = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            (await verifyContext.IndexerRuns.CountAsync()).ShouldBe(1);
            var persisted = await verifyContext.IndexerRuns.SingleAsync();
            persisted.SubmissionKey.ShouldBe("api-request-123");
            persisted.SubmissionHash.ShouldBe(new string('a', 64));
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }

        static IndexerRunEntity NewSubmission() => new()
        {
            Operation = "reindex_all",
            RequestedByUsername = "codex",
            Target = "all",
            Status = "queued",
            SubmissionKey = "api-request-123",
            SubmissionHash = new string('a', 64),
            RetrySafe = false
        };
    }

    [Fact]
    public async Task DurableMigration_ClassifiesLegacyRunningRowsAndCanRestartSafely_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_migrate_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO indexer_runs (operation, status, created_at, retry_safe)
                VALUES ('sync_schema', 'running', CURRENT_TIMESTAMP(6), FALSE),
                       ('reindex_all', 'running', CURRENT_TIMESTAMP(6), FALSE)
                """);

            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../sql/migrations/059_durable_indexer_runs.sql");
            var statements = MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(migrationPath));
            foreach (var statement in statements)
                await connection.ExecuteAsync(statement);
            foreach (var statement in statements)
                await connection.ExecuteAsync(statement);

            var rows = (await connection.QueryAsync<(string Operation, string Status, string? Error)>("""
                SELECT operation AS Operation, status AS Status, error AS Error
                FROM indexer_runs
                ORDER BY id
                """)).ToList();
            rows[0].Operation.ShouldBe("sync_schema");
            rows[0].Status.ShouldBe("queued");
            rows[1].Operation.ShouldBe("reindex_all");
            rows[1].Status.ShouldBe("failed");
            rows[1].Error.ShouldNotBeNull();
            rows[1].Error.ShouldContain("ambiguous", Case.Insensitive);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ResourceHotfixMigration_TerminalizesOnlyStartedReAnalyzeRuns_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_idx_resource_hotfix_{Guid.NewGuid():N}"
        };
        var databaseName = builder.Database;
        var runner = CreateMigrationRunner(builder.ConnectionString);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO indexer_runs
                    (operation, target, status, created_at, retry_safe, attempt_count, error_code)
                VALUES
                    ('reanalyze', 'queued-retry', 'queued', CURRENT_TIMESTAMP(6), TRUE, 1, 'rust_semantic_command_timeout'),
                    ('reanalyze', 'running-retry', 'running', CURRENT_TIMESTAMP(6), TRUE, 2, NULL),
                    ('reanalyze', 'fresh', 'queued', CURRENT_TIMESTAMP(6), TRUE, 0, NULL),
                    ('sync_schema', 'schema', 'queued', CURRENT_TIMESTAMP(6), TRUE, 1, 'indexer_operation_failed')
                """);

            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../sql/migrations/063_stop_resource_intensive_reanalysis_replays.sql");
            var statements = MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(migrationPath));
            foreach (var statement in statements)
                await connection.ExecuteAsync(statement);
            foreach (var statement in statements)
                await connection.ExecuteAsync(statement);

            var rows = (await connection.QueryAsync<(string Target, string Status, string? ErrorCode)>("""
                SELECT target AS Target, status AS Status, error_code AS ErrorCode
                FROM indexer_runs
                ORDER BY id
                """)).ToList();
            rows[0].ShouldBe(("queued-retry", "failed", "rust_semantic_command_timeout"));
            rows[1].ShouldBe(("running-retry", "failed", "reanalyze_stopped_by_resource_hotfix"));
            rows[2].ShouldBe(("fresh", "queued", null));
            rows[3].ShouldBe(("schema", "queued", "indexer_operation_failed"));
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static MariaDbMigrationRunner CreateMigrationRunner(string connectionString)
        => new(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = connectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

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
