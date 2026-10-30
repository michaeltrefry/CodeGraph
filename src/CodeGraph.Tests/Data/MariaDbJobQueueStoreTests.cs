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

public class MariaDbJobQueueStoreTests
{
    [Fact]
    public void RepositoryIdentities_CompareNormalizedSetsAndRetainUnknownLegacyWork()
    {
        var first = MySqlIndexerRunStore.RepositorySetQueueIdentity("""{"repos":[" A::/one ","b","a"]}""");
        first.ShouldNotBeNull();
        first.ShouldBe(MySqlIndexerRunStore.RepositorySetQueueIdentity("""{"repos":["B","a::https://host/a"]}"""));
        first.ShouldNotBe(MySqlIndexerRunStore.RepositorySetQueueIdentity("""{"repos":["c","d"]}"""));
        foreach (var unknown in new[] { null, "{}", "invalid", "null", "{\"repos\":[]}", "{\"repos\":[1]}", "{\"repos\":[\"::/path\"]}" })
            MySqlIndexerRunStore.RepositorySetQueueIdentity(unknown).ShouldBeNull();
        MySqlIndexerRunStore.OverlayQueueIdentity("REPO", " feature/a ")
            .ShouldBe(MySqlIndexerRunStore.OverlayQueueIdentity("repo", "feature/a"));
        MySqlIndexerRunStore.OverlayQueueIdentity("repo", "feature/a")
            .ShouldNotBe(MySqlIndexerRunStore.OverlayQueueIdentity("repo", "feature/A"));
        MySqlIndexerRunStore.OverlayQueueIdentity(null, "main").ShouldBeNull();
        MySqlIndexerRunStore.OverlayQueueIdentity("repo", new string('A', 40))
            .ShouldBe(MySqlIndexerRunStore.OverlayQueueIdentity("repo", new string('a', 40)));
    }

    [Fact]
    public async Task QueueOrdersWorkAndDismissesOnlyUnresolvedFailures_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var store = new MySqlIndexerRunStore(db);
            var now = DateTime.UtcNow;
            var queued = await AddRunAsync(store, "queued", created: now.AddHours(-5));
            var retrying = await AddRunAsync(store, "queued", created: now.AddHours(-4));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE indexer_runs SET next_attempt_at = {now.AddHours(1)}, attempt_count = 2, cancel_requested_at = {now} WHERE id = {retrying}");
            var failedOld = await AddRunAsync(store, "failed", created: now.AddHours(-3));
            var failedNew = await AddRunAsync(store, "failed", created: now.AddHours(-2));
            var running = await AddRunAsync(store, "running", created: now.AddHours(-1));
            var completed = await AddRunAsync(store, "completed", operation: "other");
            await AddRunAsync(store, "canceled");
            var activeBatch = new AnalysisBatchEntity { Repo = "a", ProviderBatchId = "active", SubmittedAt = now, Status = "submitted" };
            var failedBatch = new AnalysisBatchEntity { Repo = "b", ProviderBatchId = "failed", SubmittedAt = now.AddMinutes(-2), Status = "failed", Error = "provider expired" };
            db.AnalysisBatches.AddRange(activeBatch, failedBatch);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE analysis_batches SET requested_by_username = 'analyst' WHERE id = {activeBatch.Id}");

            var queue = await store.GetJobQueueAsync(500);
            queue.Running.ShouldBe(2); queue.Queued.ShouldBe(2); queue.Failed.ShouldBe(3);
            queue.Items.Single(x => x.Id == activeBatch.Id && x.Kind == "analysis-batch").RequestedByUsername.ShouldBe("analyst");
            queue.Items.Where(x => x.Kind == "indexer-run").Select(x => x.Id)
                .ShouldBe(new[] { running, failedNew, failedOld, queued, retrying });
            queue.Items.Single(x => x.Id == retrying && x.Kind == "indexer-run").Cancelable.ShouldBeFalse();
            queue.Items.Single(x => x.Id == retrying && x.Kind == "indexer-run").AttemptCount.ShouldBe(2);
            queue.Items.Single(x => x.Id == queued && x.Kind == "indexer-run").Cancelable.ShouldBeTrue();
            queue.Items.Single(x => x.Id == failedOld && x.Kind == "indexer-run").Error.ShouldBe("fallback detail");
            queue.Items.Single(x => x.Id == failedBatch.Id && x.Kind == "analysis-batch").Error.ShouldBe("provider expired");
            (await store.DismissJobAsync("indexer-run", failedNew)).ShouldBeTrue();
            (await store.DismissJobAsync("indexer-run", failedNew)).ShouldBeFalse();
            (await store.DismissJobAsync("indexer-run", completed)).ShouldBeFalse();
            (await store.DismissJobAsync("indexer-run", running)).ShouldBeFalse();
            (await store.DismissJobAsync("unknown", failedOld)).ShouldBeFalse();
            (await store.DismissJobAsync("analysis-batch", failedBatch.Id)).ShouldBeTrue();
            (await store.DismissJobAsync("analysis-batch", failedBatch.Id)).ShouldBeFalse();
            (await store.GetJobQueueAsync(500)).Failed.ShouldBe(1);
            // A failed later parent never hides an older one. A completed parent does.
            db.AnalysisBatches.Add(new AnalysisBatchEntity { Repo = "a", ProviderBatchId = "failed-a", SubmittedAt = now, Status = "failed", Error = "bad item" });
            await db.SaveChangesAsync();
            (await store.GetJobQueueAsync(500)).Failed.ShouldBe(2);
            db.AnalysisBatches.Add(new AnalysisBatchEntity { Repo = "a", ProviderBatchId = "good-a", SubmittedAt = now, Status = "completed" });
            await db.SaveChangesAsync();
            (await store.GetJobQueueAsync(500)).Failed.ShouldBe(1);
        });

    [Fact]
    public async Task CountsAreUncappedAndProcessRepositoryRequesterSurvives_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            db.IndexerRuns.AddRange(Enumerable.Range(0, 503).Select(i => new IndexerRunEntity
            {
                Operation = "process_repository", Status = "queued", CreatedAt = DateTime.UtcNow.AddSeconds(i),
                Target = $"repo-{i}", RequestedByUsername = "process-repository",
                ArgsJson = """{"requestedByUsername":"original-user"}"""
            }));
            await db.SaveChangesAsync();
            var queue = await new MySqlIndexerRunStore(db).GetJobQueueAsync(1000);
            queue.Items.Count.ShouldBe(500);
            queue.Queued.ShouldBe(503);
            queue.Items[0].RequestedByUsername.ShouldBe("original-user");
            var small = await new MySqlIndexerRunStore(db).GetJobQueueAsync(1);
            small.Items.Count.ShouldBe(1); small.Queued.ShouldBe(503);
        });

    [Fact]
    public async Task SupersessionUsesSemanticTargetsAndPersistsAcrossRetention_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var store = new MySqlIndexerRunStore(db);
            var old = DateTime.UtcNow.AddDays(-60);
            var nullFailure = await AddRunAsync(store, "failed", target: null, created: old);
            await AddRunAsync(store, "completed", target: null, created: old);
            var different = await AddRunAsync(store, "failed", target: "other", created: old);
            var setFailure = await AddRunAsync(store, "failed", "process_repositories", "2 repositories", old,
                """{"repos":["A","b"]}""");
            var unrelated = await AddRunAsync(store, "failed", "process_repositories", "2 repositories", old,
                """{"repos":["c","d"]}""");
            await AddRunAsync(store, "completed", "process_repositories", "2 repositories", old,
                """{"repos":["B","a"]}""");
            var unknown = await AddRunAsync(store, "failed", "process_repositories", "2 repositories", old, "{}");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO repositories (name) VALUES ('repo')");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO branch_overlays (repository,requested_ref,status,created_at)
                VALUES ('repo','feature/a','failed',UTC_TIMESTAMP(6)),('repo','feature/a','ready',UTC_TIMESTAMP(6)),
                       ('repo','feature/b','ready',UTC_TIMESTAMP(6))
                """);
            var overlayIds = (await db.Database.GetDbConnection().QueryAsync<long>("SELECT id FROM branch_overlays ORDER BY id")).ToArray();
            var overlayFailure = await AddRunAsync(store, "failed", "branch_overlay", overlayIds[0].ToString(), old);
            await AddRunAsync(store, "completed", "branch_overlay", overlayIds[2].ToString(), old);
            (await store.GetJobQueueAsync(500)).Items.ShouldContain(x => x.Kind == "indexer-run" && x.Id == overlayFailure);
            await AddRunAsync(store, "completed", "branch_overlay", overlayIds[1].ToString(), old);
            // Emulate special runs that existed before the migration, including an active row.
            await db.Database.ExecuteSqlRawAsync("UPDATE indexer_runs SET queue_work_identity = NULL");
            var queue = await store.GetJobQueueAsync(500);
            queue.Items.Where(x => x.Kind == "indexer-run").Select(x => x.Id).Order()
                .ShouldBe(new[] { different, unrelated, unknown }.Order());
            (await store.GetIndexerRunAsync(nullFailure))!.FailureResolvedAt.ShouldNotBeNull();
            (await store.GetIndexerRunAsync(setFailure))!.FailureResolvedAt.ShouldNotBeNull();
            (await store.GetIndexerRunAsync(overlayFailure))!.FailureResolvedAt.ShouldNotBeNull();
            (await store.GetIndexerRunAsync(unknown))!.QueueWorkIdentity.ShouldBe("");
            // Identity backfill never rewrites display/execution targets or the source args.
            (await store.GetIndexerRunAsync(setFailure))!.Target.ShouldBe("2 repositories");
            (await store.GetIndexerRunAsync(setFailure))!.ArgsJson.ShouldBe("""{"repos":["A","b"]}""");
            // Delete all resolved evidence, bounded by the supplied page size.
            int deleted;
            do { deleted = await store.DeleteFinishedIndexerRunsBeforeAsync(DateTime.UtcNow.AddDays(-30), 2); deleted.ShouldBeLessThanOrEqualTo(2); }
            while (deleted == 2);
            (await store.GetJobQueueAsync(500)).Items.Select(x => x.Id).Order()
                .ShouldBe(new[] { different, unrelated, unknown }.Order());
        });

    [Fact]
    public async Task RetentionPreservesActiveUnresolvedAndLateFailureWitnesses_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var store = new MySqlIndexerRunStore(db);
            var old = DateTime.UtcNow.AddDays(-60);
            var active = await AddRunAsync(store, "running", target: "same", created: old);
            var witness = await AddRunAsync(store, "completed", target: "same", created: old);
            var queued = await AddRunAsync(store, "queued", target: "queued", created: old);
            var unresolved = await AddRunAsync(store, "failed", target: "unresolved", created: old);
            var dismissed = await AddRunAsync(store, "failed", target: "dismissed", created: old);
            (await store.DismissJobAsync("indexer-run", dismissed)).ShouldBeTrue();
            await AddRunAsync(store, "canceled", created: old);
            await AddRunAsync(store, "completed", target: "fresh");
            (await store.DeleteFinishedIndexerRunsBeforeAsync(DateTime.UtcNow.AddDays(-30), 5000)).ShouldBe(2);
            (await store.GetIndexerRunAsync(active)).ShouldNotBeNull();
            (await store.GetIndexerRunAsync(witness)).ShouldNotBeNull();
            (await store.GetIndexerRunAsync(queued)).ShouldNotBeNull();
            (await store.GetIndexerRunAsync(unresolved)).ShouldNotBeNull();
            await store.UpdateIndexerRunStatusAsync(active, "failed", completedAt: old);
            (await store.DeleteFinishedIndexerRunsBeforeAsync(DateTime.UtcNow.AddDays(-30), 5000)).ShouldBe(2);
            (await store.GetJobQueueAsync(500)).Items.ShouldNotContain(x => x.Id == active && x.Kind == "indexer-run");
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.DeleteFinishedIndexerRunsBeforeAsync(old, 0));
        });

    [Fact]
    public async Task ScheduleFailuresSurviveRetryAndFencingAcrossBothCompletionBranches_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var store = new MySqlJobScheduleStore(db);
            var queue = new MySqlIndexerRunStore(db);
            var now = DateTime.UtcNow;
            var schedule = await store.CreateScheduleAsync(new JobScheduleEntity
            {
                Name = "docs", JobType = "RegenerateMcpDocs", CronExpression = "0 * * * *", IsEnabled = true,
                NextRunUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var first = (await store.TryAcquireScheduleAsync(schedule.Id, now, "first", TimeSpan.FromMinutes(5)))!;
            (await store.MarkRunStartedAsync(schedule.Id, now, now, "first")).ShouldBeTrue();
            (await queue.GetJobQueueAsync(500)).Running.ShouldBe(1);
            (await store.MarkRunCompletedAsync(schedule.Id, now, null, "failed", "original error", now, first.ScheduleRevision, "first")).ShouldBeTrue();
            (await queue.GetJobQueueAsync(500)).Failed.ShouldBe(1);
            var retry = (await store.TryAcquireScheduleAsync(schedule.Id, now, "retry", TimeSpan.FromMinutes(5)))!;
            (await store.MarkRunStartedAsync(schedule.Id, now, now, "retry")).ShouldBeTrue();
            var retryQueue = await queue.GetJobQueueAsync(500);
            retryQueue.Running.ShouldBe(1); retryQueue.Failed.ShouldBe(1);
            retryQueue.Items.Single(x => x.Status == "failed").Error.ShouldBe("original error");
            (await store.MarkRunCompletedAsync(schedule.Id, now, null, "succeeded", null, now, first.ScheduleRevision, "first")).ShouldBeFalse();
            // Expired retry loses ownership without resolving or losing the prior failure.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_schedules SET lease_expires_utc = {now.AddSeconds(-1)} WHERE id = {schedule.Id}");
            (await queue.GetJobQueueAsync(500)).Running.ShouldBe(0);
            (await queue.GetJobQueueAsync(500)).Failed.ShouldBe(1);
            (await queue.DismissJobAsync("scheduled-job", schedule.Id)).ShouldBeTrue();
            (await queue.DismissJobAsync("scheduled-job", schedule.Id)).ShouldBeFalse();
            var third = (await store.TryAcquireScheduleAsync(schedule.Id, now, "third", TimeSpan.FromMinutes(5)))!;
            (await store.MarkRunCompletedAsync(schedule.Id, now, now.AddHours(1), "failed", "new error", now, third.ScheduleRevision, "third")).ShouldBeTrue();
            (await queue.GetJobQueueAsync(500)).Items.Single(x => x.Status == "failed").Error.ShouldBe("new error");
            var fourth = (await store.TryAcquireScheduleAsync(schedule.Id, now, "fourth", TimeSpan.FromMinutes(5)))!;
            (await store.MarkRunCompletedAsync(schedule.Id, now, now.AddHours(1), "succeeded", null, now, fourth.ScheduleRevision, "fourth")).ShouldBeTrue();
            (await queue.GetJobQueueAsync(500)).Items.ShouldBeEmpty();
            // Wrappers expose failed submissions even without a durable indexer run, but not their lease.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_schedules SET job_type = 'Discover' WHERE id = {schedule.Id}");
            var wrapper = (await store.TryAcquireScheduleAsync(schedule.Id, now, "wrapper", TimeSpan.FromMinutes(5)))!;
            (await queue.GetJobQueueAsync(500)).Items.ShouldBeEmpty();
            (await store.MarkRunCompletedAsync(schedule.Id, now, null, "failed", "dispatch failed", now, wrapper.ScheduleRevision, "wrapper")).ShouldBeTrue();
            var wrapperQueue = await queue.GetJobQueueAsync(500);
            wrapperQueue.Failed.ShouldBe(1);
            wrapperQueue.Items.Single().Operation.ShouldBe("Discover");
            wrapperQueue.Items.Single().Error.ShouldBe("dispatch failed");
        });

    [Fact]
    public async Task VisibilityMigrationDismissesHistoricalFailuresAndRestartsSafely_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var store = new MySqlIndexerRunStore(db);
            var failed = await AddRunAsync(store, "failed");
            var active = await AddRunAsync(store, "running", "process_repositories", args: """{"repos":["a"]}""");
            db.AnalysisBatches.Add(new AnalysisBatchEntity { Repo = "a", ProviderBatchId = "old", Status = "failed", SubmittedAt = DateTime.UtcNow });
            db.JobSchedules.Add(new JobScheduleEntity
            {
                Name = "old", JobType = "Discover", CronExpression = "0 * * * *", LastRunStatus = "failed",
                LastError = "old dispatch", NextRunUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "../../../../../sql/migrations/093_job_queue_visibility.sql"));
            for (var attempt = 0; attempt < 2; attempt++)
                foreach (var statement in MariaDbMigrationRunner.SplitStatements(script))
                    await db.Database.ExecuteSqlRawAsync(statement);
            (await store.GetIndexerRunAsync(failed))!.DismissedAt.ShouldNotBeNull();
            (await store.GetIndexerRunAsync(active))!.DismissedAt.ShouldBeNull();
            var queue = await store.GetJobQueueAsync(500);
            queue.Failed.ShouldBe(0); queue.Running.ShouldBe(1);
        });

    [Fact]
    public async Task RetentionScheduleSeedIsRestartSafeAndPreservesOperatorSettings_WhenConnectionIsConfigured()
        => await WithDatabaseAsync(async db =>
        {
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "../../../../../sql/migrations/094_default_indexer_run_retention_schedule.sql"));
            async Task ApplySeed()
            {
                foreach (var statement in MariaDbMigrationRunner.SplitStatements(script))
                    await db.Database.GetDbConnection().ExecuteAsync(statement);
            }
            await ApplySeed();
            await ApplySeed();
            var schedule = (await db.JobSchedules.AsNoTracking().ToListAsync()).Single();
            schedule.JobType.ShouldBe("IndexerRunRetention");
            schedule.IsEnabled.ShouldBeTrue();
            schedule.CronExpression.ShouldBe("0 4 * * 0");
            schedule.TimeZoneId.ShouldBe("UTC");
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE job_schedules SET name = 'operator-retention', is_enabled = FALSE,
                    args_json = JSON_OBJECT('retentionDays',7), cron_expression = '0 2 * * 1'
                """);
            await ApplySeed();
            var preserved = (await db.JobSchedules.AsNoTracking().ToListAsync()).Single();
            preserved.Name.ShouldBe("operator-retention");
            preserved.IsEnabled.ShouldBeFalse();
            preserved.ArgsJson.ShouldContain("7");
            preserved.CronExpression.ShouldBe("0 2 * * 1");
        });

    private static Task<long> AddRunAsync(MySqlIndexerRunStore store, string status, string operation = "link",
        string? target = "all", DateTime? created = null, string? args = null)
        => store.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = operation, Status = status, Target = target, ArgsJson = args, Message = "fallback detail",
            CreatedAt = created ?? DateTime.UtcNow, CompletedAt = status is "completed" or "failed" or "canceled" ? created ?? DateTime.UtcNow : null
        });

    private static async Task WithDatabaseAsync(Func<CodeGraphDbContext, Task> test)
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
        { Database = $"cg_queue_{Guid.NewGuid():N}" };
        try
        {
            await new MariaDbMigrationRunner(Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }), NullLogger<MariaDbMigrationRunner>.Instance).ApplyConfiguredMigrationsAsync();
            var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb)).Options;
            await using var db = new CodeGraphDbContext(options);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM job_schedules");
            await test(db);
        }
        finally
        {
            var database = builder.Database;
            builder.Database = "";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await MySqlConnection.ClearAllPoolsAsync();
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
        }
    }
}
