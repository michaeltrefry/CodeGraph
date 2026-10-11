using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// 068_default_batch_analysis_schedule.sql (sc-25698): submitted analysis batches are finished by
/// the periodic ProcessBatchAnalysis job, so every installation gets one unless an operator already
/// configured it.
/// </summary>
public sealed class MariaDbBatchAnalysisScheduleMigrationTests
{
    private const string ScheduleMigration = "068_default_batch_analysis_schedule.sql";

    [Fact]
    public async Task Migration_SeedsAnEnabledFifteenMinuteSweep()
    {
        await using var db = await MigrationDatabase.CreateAsync(includeScheduleMigration: true);

        var schedules = await db.ProcessBatchAnalysisSchedulesAsync();

        var schedule = schedules.ShouldHaveSingleItem();
        schedule.Name.ShouldBe("process-batch-analysis");
        schedule.IsEnabled.ShouldBeTrue();
        schedule.CronExpression.ShouldBe("*/15 * * * *");
    }

    [Fact]
    public async Task Migration_LeavesAnOperatorsExistingScheduleAlone()
    {
        await using var db = await MigrationDatabase.CreateAsync(includeScheduleMigration: false);
        await db.ExecuteAsync("""
            INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
            VALUES ('batches', 'ProcessBatchAnalysis', FALSE, '0 * * * *', 'UTC', '{}', UTC_TIMESTAMP(3))
            """);

        await db.ApplyScheduleMigrationAsync();

        var schedule = (await db.ProcessBatchAnalysisSchedulesAsync()).ShouldHaveSingleItem();
        schedule.Name.ShouldBe("batches");
        schedule.IsEnabled.ShouldBeFalse();
    }

    private sealed record ScheduleRow(string Name, bool IsEnabled, string CronExpression);

    private sealed class MigrationDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _sourceMigrations = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        private readonly string _migrationsPath =
            Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");

        private MigrationDatabase(string connectionString) => _connectionString = connectionString;

        public static async Task<MigrationDatabase> CreateAsync(bool includeScheduleMigration)
        {
            var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
            {
                Database = $"cg_mig_068_{Guid.NewGuid():N}"
            };
            var db = new MigrationDatabase(builder.ConnectionString);
            Directory.CreateDirectory(db._migrationsPath);
            foreach (var file in Directory.EnumerateFiles(db._sourceMigrations, "*.sql")
                         .Where(file => includeScheduleMigration ||
                                        string.CompareOrdinal(Path.GetFileName(file), ScheduleMigration) < 0))
            {
                File.Copy(file, Path.Combine(db._migrationsPath, Path.GetFileName(file)));
            }

            await db.MigrateAsync();
            return db;
        }

        public async Task ApplyScheduleMigrationAsync()
        {
            File.Copy(Path.Combine(_sourceMigrations, ScheduleMigration), Path.Combine(_migrationsPath, ScheduleMigration));
            await MigrateAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.ExecuteAsync(sql);
        }

        public async Task<IReadOnlyList<ScheduleRow>> ProcessBatchAnalysisSchedulesAsync()
        {
            await using var connection = new MySqlConnection(_connectionString);
            return (await connection.QueryAsync<ScheduleRow>("""
                SELECT name AS Name, is_enabled AS IsEnabled, cron_expression AS CronExpression
                FROM job_schedules WHERE job_type = 'ProcessBatchAnalysis'
                """)).ToList();
        }

        private Task MigrateAsync() =>
            new MariaDbMigrationRunner(
                    Options.Create(new MariaDbStorageOptions
                    {
                        ConnectionString = _connectionString,
                        MigrationsPath = _migrationsPath,
                        MigrationLockTimeoutSeconds = 30
                    }),
                    NullLogger<MariaDbMigrationRunner>.Instance)
                .ApplyConfiguredMigrationsAsync();

        public async ValueTask DisposeAsync()
        {
            Directory.Delete(_migrationsPath, recursive: true);
            var builder = new MySqlConnectionStringBuilder(_connectionString);
            var database = builder.Database;
            builder.Database = "";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
        }
    }
}
