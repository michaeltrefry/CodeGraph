using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models.Work;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Freshness (sc-27869): a knowledge entry verified against files of an indexed repository reads
/// possibly_stale after a real index run picks up a change to one of them, from the content hashes
/// the indexer keeps (file_hashes), and not when only other files change. Verifying clears it.
/// </summary>
public sealed class KnowledgeFreshnessMariaDbTests : IAsyncLifetime
{
    private const string Repository = "KnowledgeFresh";
    private const string GreeterPath = "Sources/App/Greeter.swift";
    private const string MainPath = "Sources/App/Main.swift";
    private const string LaterPath = "Sources/App/Later.swift";

    private string connectionString = "";
    private string root = "";
    private MariaDbMigrationRunner runner = null!;
    private IOptions<MariaDbStorageOptions> storageOptions = null!;
    private WorkService service = null!;

    public async Task InitializeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
        {
            Database = $"cg_knowledge_fresh_{Guid.NewGuid():N}"
        };
        connectionString = builder.ConnectionString;
        storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = connectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        await runner.ApplyConfiguredMigrationsAsync();

        root = Path.Combine(Path.GetTempPath(), $"codegraph-knowledge-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources", "App"));
        await File.WriteAllTextAsync(Path.Combine(root, GreeterPath), "struct Greeter {\n    func greet(name: String) -> String {\n        return name\n    }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(root, MainPath), "func run() {\n    let greeter = Greeter()\n    _ = greeter.greet(name: \"a\")\n}\n");

        var store = new MySqlWorkStore(storageOptions);
        service = new WorkService(store);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        builder.Database = "";
        await MySqlConnection.ClearAllPoolsAsync();
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
    }

    // One index run of the fixture repository, as the indexer runs it (incremental by content hash).
    private async Task IndexAsync(string sha)
    {
        var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(connectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;
        await using var context = new CodeGraphDbContext(dbOptions);
        var store = new MySqlGraphStore(
            context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
            new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
            new MySqlReviewStore(context), runner);
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repository, LocalPath = root, LastCommitSha = sha });
        await new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexRepositoryAsync(Repository, root, ct: CancellationToken.None);
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repository, LocalPath = root, LastCommitSha = sha });
        await store.UpsertSyncStateAsync(new SyncStateEntity { Repository = Repository, LastCommitSha = sha });
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments)
    {
        var result = await service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor("alice"));
        return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
    }

    private async Task<JsonElement> EntryAsync(string id) =>
        (await CallAsync(WorkTools.Recall, new { id })).GetProperty("items")[0];

    [Fact]
    public async Task Changing_a_verified_file_flags_the_entry_possibly_stale_after_the_next_index()
    {
        await IndexAsync(new string('a', 40));
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key = "FRESH", name = "Fresh", repositories = new[] { new { repository = Repository, role = "owned" } } } });

        var greeter = (await CallAsync(WorkTools.Record, new
        {
            project = "FRESH",
            entry = new { kind = "decision", summary = "Greeter returns the name unchanged", repositories = new[] { Repository }, paths = new[] { GreeterPath } }
        })).GetProperty("entry");
        greeter.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
        greeter.GetProperty("verified_commit").GetString().ShouldBe(new string('a', 40));
        greeter.GetProperty("verified_files").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe(GreeterPath);
        var main = (await CallAsync(WorkTools.Record, new
        {
            project = "FRESH",
            entry = new { kind = "constraint", summary = "run builds one Greeter", repositories = new[] { Repository }, paths = new[] { MainPath } }
        })).GetProperty("entry");
        // A file not indexed yet is baselined by its first index, not flagged by it.
        var later = (await CallAsync(WorkTools.Record, new
        {
            project = "FRESH",
            entry = new { kind = "fact", summary = "Later is added next", repositories = new[] { Repository }, paths = new[] { LaterPath } }
        })).GetProperty("entry");

        // Nothing changed: a second index flags nothing.
        await IndexAsync(new string('b', 40));
        (await EntryAsync(greeter.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();

        // Greeter.swift changes and Later.swift appears; the next index run picks both up.
        await File.WriteAllTextAsync(Path.Combine(root, GreeterPath), "struct Greeter {\n    func greet(name: String) -> String {\n        return \"Hello \" + name\n    }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(root, LaterPath), "func later() {}\n");
        await IndexAsync(new string('c', 40));

        var stale = await EntryAsync(greeter.GetProperty("id").GetString()!);
        stale.GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
        stale.GetProperty("stale_files").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe(GreeterPath);
        (await EntryAsync(main.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
        (await EntryAsync(later.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();

        // recall rows carry the flag, and can filter on it.
        var flagged = (await CallAsync(WorkTools.Recall, new { project = "FRESH", possibly_stale = true })).GetProperty("items").EnumerateArray().ToList();
        flagged.Single().GetProperty("id").GetString().ShouldBe(greeter.GetProperty("id").GetString());
        flagged[0].GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();

        // Re-verifying makes the changed content the baseline.
        var verified = (await CallAsync(WorkTools.Record, new { verify = new { id = greeter.GetProperty("id").GetString(), expected_version = 1 } })).GetProperty("entry");
        verified.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
        verified.GetProperty("verified_commit").GetString().ShouldBe(new string('c', 40));

        // Later.swift was baselined by its first index: changing it now flags its entry.
        await File.WriteAllTextAsync(Path.Combine(root, LaterPath), "func later() { run() }\n");
        await IndexAsync(new string('d', 40));
        (await EntryAsync(later.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
        (await EntryAsync(greeter.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_file_changed_twice_with_no_read_in_between_is_flagged_and_reads_write_nothing()
    {
        await IndexAsync(new string('a', 40));
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key = "TWICE", name = "Twice", repositories = new[] { new { repository = Repository, role = "owned" } } } });
        var entry = (await CallAsync(WorkTools.Record, new
        {
            project = "TWICE",
            entry = new { kind = "fact", summary = "Later is added next", repositories = new[] { Repository }, paths = new[] { LaterPath } }
        })).GetProperty("entry").GetProperty("id").GetString()!;

        // Added, indexed, changed, indexed, changed, indexed: no knowledge read in between.
        await File.WriteAllTextAsync(Path.Combine(root, LaterPath), "func later() {}\n");
        await IndexAsync(new string('b', 40));
        await File.WriteAllTextAsync(Path.Combine(root, LaterPath), "func later() { run() }\n");
        await IndexAsync(new string('c', 40));
        await File.WriteAllTextAsync(Path.Combine(root, LaterPath), "func later() { run(); run() }\n");
        await IndexAsync(new string('d', 40));

        // The baseline is the first index's hash, so the entry is flagged; the read wrote nothing.
        var before = await FilesSnapshotAsync();
        (await EntryAsync(entry)).GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
        await CallAsync(WorkTools.Recall, new { project = "TWICE", paths = new[] { LaterPath } });
        (await FilesSnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task An_entry_scoped_to_a_graph_node_is_flagged_when_the_nodes_file_changes()
    {
        await IndexAsync(new string('a', 40));
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key = "NODES", name = "Nodes", repositories = new[] { new { repository = Repository, role = "owned" } } } });
        long greet;
        await using (var connection = new MySqlConnection(connectionString))
            greet = await connection.ExecuteScalarAsync<long>("SELECT id FROM nodes WHERE repository = @Repository AND name = 'greet'", new { Repository });

        var entry = (await CallAsync(WorkTools.Record, new { project = "NODES", entry = new { kind = "decision", summary = "greet stays pure", nodes = new[] { greet } } }))
            .GetProperty("entry");
        entry.GetProperty("nodes").EnumerateArray().Single().GetProperty("qualified_name").GetString()!.ShouldContain("greet");
        entry.GetProperty("verified_files").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe(GreeterPath);
        entry.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
        (await CallAsync(WorkTools.Recall, new { project = "NODES", nodes = new[] { greet } })).GetProperty("items").GetArrayLength().ShouldBe(1);

        await File.WriteAllTextAsync(Path.Combine(root, GreeterPath), "struct Greeter {\n    func greet(name: String) -> String {\n        return name + \"!\"\n    }\n}\n");
        await IndexAsync(new string('b', 40));
        (await EntryAsync(entry.GetProperty("id").GetString()!)).GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reads_neither_fail_nor_wait_while_an_index_write_holds_file_hashes_rows()
    {
        await IndexAsync(new string('a', 40));
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key = "LOCKS", name = "Locks", repositories = new[] { new { repository = Repository, role = "owned" } } } });
        await CallAsync(WorkTools.CreateWork, new { project = "LOCKS", item = new { title = "Item", repository = Repository } });
        await CallAsync(WorkTools.Record, new
        {
            project = "LOCKS",
            entry = new { kind = "constraint", summary = "Pending baseline", repositories = new[] { Repository }, paths = new[] { "Sources/App/Pending.swift" } }
        });

        // An index write in flight: new and changed file_hashes rows, X-locked until it ends.
        await using var index = new MySqlConnection(connectionString);
        await index.OpenAsync();
        await using var transaction = await index.BeginTransactionAsync();
        await index.ExecuteAsync(
            "INSERT INTO file_hashes (repository, rel_path, content_hash) VALUES (@Repository, 'Sources/App/Pending.swift', 'pending')", new { Repository }, transaction);
        await index.ExecuteAsync("UPDATE file_hashes SET content_hash = 'changing' WHERE repository = @Repository", new { Repository }, transaction);

        var reads = Task.WhenAll(
            CallAsync(WorkTools.Recall, new { project = "LOCKS", paths = new[] { "Sources/App" } }),
            CallAsync(WorkTools.GetWork, new { id = "LOCKS-1" }),
            CallAsync(WorkTools.StartSession, new { project = "LOCKS", harness = "codex", repository = Repository, paths = new[] { "Sources/App/Pending.swift" } }));
        (await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(3)))).ShouldBe(reads, "the reads waited on the index write");
        var answers = await reads;
        answers[0].GetProperty("items").GetArrayLength().ShouldBe(1);
        answers[2].GetProperty("briefing").TryGetProperty("knowledge", out var found).ShouldBeTrue(answers[2].GetRawText());
        found.GetArrayLength().ShouldBe(1);
        await transaction.RollbackAsync();
    }

    private async Task<List<(string Path, string? Hash)>> FilesSnapshotAsync()
    {
        await using var connection = new MySqlConnection(connectionString);
        return (await connection.QueryAsync<(string Path, string? Hash)>("SELECT path, content_hash FROM knowledge_entry_files ORDER BY id")).ToList();
    }
}
