using System.Diagnostics;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Knowledge freshness baselines (sc-27869) are adopted after commit, best effort, by both the index
/// write and the knowledge write: a locked, missing or racing knowledge table never fails or rolls
/// back an index write, separators and node files are matched, and a record that races an index
/// run is baselined by its own post-commit adoption.
/// </summary>
public sealed class KnowledgeBaselinesMariaDbTests : IAsyncLifetime
{
    private const string Repository = "KnowledgeBase";
    private string connectionString = "";
    private IOptions<MariaDbStorageOptions> storageOptions = null!;
    private MariaDbMigrationRunner runner = null!;
    private WorkService service = null!;
    private readonly ListLogger logger = new();

    public async Task InitializeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString()) { Database = $"cg_kbase_{Guid.NewGuid():N}" };
        connectionString = builder.ConnectionString;
        storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = connectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        await runner.ApplyConfiguredMigrationsAsync();
        await using var connection = new MySqlConnection(connectionString);
        await connection.ExecuteAsync("INSERT INTO repositories (name) VALUES (@Repository)", new { Repository });
        service = new WorkService(new MySqlWorkStore(storageOptions));
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key = "KBASE", name = "Baselines", repositories = new[] { new { repository = Repository, role = "owned" } } } });
    }

    public async Task DisposeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        builder.Database = "";
        await MySqlConnection.ClearAllPoolsAsync();
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments)
    {
        var result = await service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor("alice"));
        return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
    }

    private async Task<T> WithGraphStoreAsync<T>(Func<MySqlGraphStore, Task<T>> body)
    {
        var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(connectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb)).Options;
        await using var context = new CodeGraphDbContext(dbOptions);
        var store = new MySqlGraphStore(context, storageOptions, logger, new MySqlAnalysisStore(context), new MySqlMetricsStore(context), new MySqlReviewStore(context), runner);
        return await body(store);
    }

    // A file write as each of the three index paths makes it.
    private Task<int> WriteAsync(string how, string path, string hash) => WithGraphStoreAsync(async store =>
    {
        var hashes = new Dictionary<string, string> { [path] = hash };
        switch (how)
        {
            case "files":
                return await store.ReplaceProjectFilesAsync(Repository, [path], [], [], hashes);
            case "graph":
                return await store.ReplaceProjectGraphAsync(Repository, [], [], hashes, new RepositoryEntity { Name = Repository }, null);
            default:
                await store.UpsertFileHashBatchAsync(Repository, hashes);
                return 0;
        }
    });

    private async Task<string> RecordAsync(object entry) =>
        (await CallAsync(WorkTools.Record, new { project = "KBASE", entry })).GetProperty("entry").GetProperty("id").GetString()!;

    private async Task<(string Path, string? Hash)> FileOfAsync(string entryId)
    {
        await using var connection = new MySqlConnection(connectionString);
        return await connection.QuerySingleAsync<(string, string?)>("SELECT path, content_hash FROM knowledge_entry_files WHERE entry_id = @entryId", new { entryId });
    }

    [Theory]
    [InlineData("files")]
    [InlineData("graph")]
    [InlineData("batch")]
    public async Task An_index_write_adopts_a_pending_baseline_after_it_commits(string how)
    {
        var entry = await RecordAsync(new { kind = "constraint", summary = $"Pending {how}", repositories = new[] { Repository }, paths = new[] { "src/New/File.cs" } });
        (await FileOfAsync(entry)).ShouldBe(("src/New/File.cs", (string?)null));

        await WriteAsync(how, "src/New/File.cs", "h1");

        (await FileOfAsync(entry)).ShouldBe(("src/New/File.cs", (string?)"h1"));
    }

    [Fact]
    public async Task A_backslash_index_write_baselines_a_forward_slash_pending_row_ignoring_case_and_takes_its_spelling()
    {
        var entry = await RecordAsync(new { kind = "constraint", summary = "Windows path", repositories = new[] { Repository }, paths = new[] { "src/Win/Main.cs" } });

        await WriteAsync("batch", "SRC\\Win\\Main.cs", "hw");

        (await FileOfAsync(entry)).ShouldBe(("SRC\\Win\\Main.cs", (string?)"hw"));
        var view = (await CallAsync(WorkTools.Recall, new { id = entry })).GetProperty("items")[0];
        view.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
        await WriteAsync("batch", "SRC\\Win\\Main.cs", "hw2");
        (await CallAsync(WorkTools.Recall, new { id = entry })).GetProperty("items")[0].GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_node_scoped_entry_recorded_before_its_file_is_indexed_is_baselined_by_the_index_write()
    {
        long node;
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.ExecuteAsync(
                "INSERT INTO nodes (project, label, name, qualified_name, file_path) VALUES (@Repository, 'Method', 'Run', 'App.Run', 'src/App/Run.cs')", new { Repository });
            node = await connection.ExecuteScalarAsync<long>("SELECT id FROM nodes WHERE qualified_name = 'App.Run'");
        }

        var entry = await RecordAsync(new { kind = "decision", summary = "Run stays synchronous", nodes = new[] { node } });
        (await FileOfAsync(entry)).ShouldBe(("src/App/Run.cs", (string?)null));

        await WriteAsync("files", "src/App/Run.cs", "hn");
        (await FileOfAsync(entry)).Hash.ShouldBe("hn");
        await WriteAsync("files", "src/App/Run.cs", "hn2");
        (await CallAsync(WorkTools.Recall, new { id = entry })).GetProperty("items")[0].GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("files")]
    [InlineData("graph")]
    public async Task An_index_write_succeeds_while_another_transaction_holds_the_knowledge_rows(string how)
    {
        var entry = await RecordAsync(new { kind = "constraint", summary = $"Locked {how}", repositories = new[] { Repository }, paths = new[] { "src/Locked.cs" } });
        await using var holder = new MySqlConnection(connectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("DELETE FROM knowledge_entry_files", transaction: transaction);

        var clock = Stopwatch.StartNew();
        await WriteAsync(how, "src/Locked.cs", "hl");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        await transaction.RollbackAsync();

        // The index write is stored; the adoption was skipped and logged, the row still pending.
        await using var connection = new MySqlConnection(connectionString);
        (await connection.ExecuteScalarAsync<string>("SELECT content_hash FROM file_hashes WHERE project = @Repository AND rel_path = 'src/Locked.cs'", new { Repository })).ShouldBe("hl");
        (await FileOfAsync(entry)).Hash.ShouldBeNull();
        logger.Warnings.ShouldContain(message => message.Contains("not adopted"));
    }

    [Fact]
    public async Task An_index_write_succeeds_without_the_knowledge_table()
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.ExecuteAsync("RENAME TABLE knowledge_entry_files TO knowledge_entry_files_hidden");
        try
        {
            await WriteAsync("files", "src/Any.cs", "ha");
            await WriteAsync("graph", "src/Any.cs", "ha2");
            (await connection.ExecuteScalarAsync<string>("SELECT content_hash FROM file_hashes WHERE project = @Repository AND rel_path = 'src/Any.cs'", new { Repository })).ShouldBe("ha2");
            logger.Warnings.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await connection.ExecuteAsync("RENAME TABLE knowledge_entry_files_hidden TO knowledge_entry_files");
        }
    }

    [Fact]
    public async Task A_record_that_races_an_index_write_is_baselined_by_its_own_post_commit_adoption()
    {
        // The record's transaction reads file_hashes before the index write commits F ...
        await using var record = new MySqlConnection(connectionString);
        await record.OpenAsync();
        await using var transaction = await record.BeginTransactionAsync();
        // The first read fixes the transaction's REPEATABLE READ snapshot.
        (await record.ExecuteScalarAsync<string?>("SELECT content_hash FROM file_hashes WHERE project = @Repository AND rel_path = 'src/Race.cs'", new { Repository }, transaction)).ShouldBeNull();

        // ... the index write commits F and adopts: the record's row is not committed yet, so it sees none ...
        await WriteAsync("batch", "src/Race.cs", "hr");

        // ... then the record stores its row with a NULL hash and commits.
        var entryId = Guid.NewGuid();
        var projectId = await record.ExecuteScalarAsync<Guid>("SELECT id FROM work_projects WHERE project_key = 'KBASE'", transaction: transaction);
        await record.ExecuteAsync(
            "INSERT INTO knowledge_entries (id, project_id, kind, summary, summary_hash, confidence, verified_at, author, created_at, updated_at) " +
            "VALUES (@entryId, @projectId, 'fact', 'Race', SHA2('race', 256), 'medium', UTC_TIMESTAMP(6), 'alice', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
            new { entryId, projectId }, transaction);
        await record.ExecuteAsync(
            "INSERT INTO knowledge_entry_files (entry_id, repository_name, path, content_hash) VALUES (@entryId, @Repository, 'src/Race.cs', NULL)",
            new { entryId, Repository }, transaction);
        await transaction.CommitAsync();
        (await FileOfAsync(entryId.ToString())).Hash.ShouldBeNull();

        // The record's post-commit adoption (what every knowledge write runs) sees F.
        await KnowledgeBaselines.AdoptPendingAsync(connectionString, [entryId], NullLogger.Instance, CancellationToken.None);
        (await FileOfAsync(entryId.ToString())).Hash.ShouldBe("hr");
    }

    private sealed class ListLogger : ILogger<MySqlGraphStore>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (Warnings)
                    Warnings.Add(formatter(state, exception));
        }
    }
}
