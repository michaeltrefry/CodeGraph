using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// sc-25698: the routine (file-slice) re-index of a large repository failed every time because its
/// graph write ran under MySqlConnector's 30 s default command timeout, and the cascaded node delete
/// alone took minutes. These tests shrink the connection default to 1 s so a modest graph reproduces it.
/// </summary>
public class MariaDbLargeGraphWriteTests
{
    private const string Repository = "inference";
    private const int NodeCount = 5_000;
    private const int EdgeCount = 30_000;
    private const int FileCount = 100;

    [Fact]
    public async Task ReplaceRepositoryFilesAsync_SlowStatementOutlivesTheConnectionDefaultCommandTimeout()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (nodes, edges, hashes) = BuildGraph();
        await db.Seed(nodes, edges, hashes);

        // Production: the slice delete cascaded to ~900k edges and ran for minutes. Here a row lock
        // held for 3 s makes the node delete slower than the store connection's 1 s default.
        var store = db.Store(new MariaDbStorageOptions());
        await using var _ = store.Context;
        var release = await db.LockFirstNodeAsync(TimeSpan.FromSeconds(3));
        var written = await store.Value.ReplaceRepositoryFilesAsync(
            Repository, hashes.Keys.ToList(), nodes, edges, Rehash(hashes));
        await release;

        written.ShouldBe(EdgeCount);
        (await db.CountAsync("nodes")).ShouldBe(NodeCount);
        (await db.CountAsync("edges")).ShouldBe(EdgeCount);
        (await db.ScalarAsync<long>("SELECT COUNT(*) FROM file_hashes WHERE content_hash LIKE '%-new'"))
            .ShouldBe(FileCount);
    }

    [Fact]
    public async Task ReplaceRepositoryFilesAsync_TimedOutWriteSurfacesTheTimeoutAndRollsBack()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (nodes, edges, hashes) = BuildGraph();
        await db.Seed(nodes, edges, hashes);

        var store = db.StoreBreakingOnTimeout(new MariaDbStorageOptions { GraphWriteCommandTimeoutSeconds = 1 });
        await using var _ = store.Context;
        var release = await db.LockFirstNodeAsync(TimeSpan.FromSeconds(4));
        var failure = await Should.ThrowAsync<MySqlException>(() => store.Value.ReplaceRepositoryFilesAsync(
            Repository, hashes.Keys.ToList(), nodes, edges, Rehash(hashes)));
        await release;

        failure.ErrorCode.ShouldBe(MySqlErrorCode.CommandTimeoutExpired);
        (await db.CountAsync("nodes")).ShouldBe(NodeCount);
        (await db.CountAsync("edges")).ShouldBe(EdgeCount);
        (await db.ScalarAsync<long>("SELECT COUNT(*) FROM file_hashes WHERE content_hash LIKE '%-new'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task ReplaceRepositoryFilesAsync_DeletesReplacedNodesInBoundedBatches()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (nodes, edges, hashes) = BuildGraph();
        await db.Seed(nodes, edges, hashes);

        var store = db.Store(new MariaDbStorageOptions { GraphDeleteBatchSize = 100 });
        await using var _ = store.Context;
        var deletesBefore = await db.GlobalDeleteStatementCountAsync();
        await store.Value.ReplaceRepositoryFilesAsync(Repository, hashes.Keys.ToList(), nodes, edges, Rehash(hashes));
        var deletes = await db.GlobalDeleteStatementCountAsync() - deletesBefore;

        // 5,000 nodes / 100 per batch = 50 batches of 4 deletes (analysis, outgoing, incoming, nodes).
        // Other tests can only add to the server-wide counter; an unbatched write issues ~5.
        deletes.ShouldBeGreaterThanOrEqualTo(200);
        (await db.CountAsync("nodes")).ShouldBe(NodeCount);
        (await db.CountAsync("edges")).ShouldBe(EdgeCount);
    }

    [Fact]
    public async Task SnapshotWrites_InsertEdgesInSourceOrder()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (nodes, edges, hashes) = BuildGraph();
        await db.Seed(nodes, edges, hashes);
        (await db.EdgeSourcesOutOfInsertOrderAsync()).ShouldBe(0);

        var store = db.Store(new MariaDbStorageOptions());
        await using var _ = store.Context;
        await store.Value.ReplaceRepositoryFilesAsync(Repository, hashes.Keys.ToList(), nodes, edges, Rehash(hashes));

        (await db.CountAsync("edges")).ShouldBe(EdgeCount);
        (await db.EdgeSourcesOutOfInsertOrderAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QualifiedNameLookups_SeekTheDigestIndexAndMatchExactly()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (nodes, edges, hashes) = BuildGraph();
        await db.Seed(nodes, edges, hashes);
        var store = db.Store(new MariaDbStorageOptions());
        await using var _ = store.Context;
        await store.Context.Database.OpenConnectionAsync();
        var connection = (MySqlConnection)store.Context.Database.GetDbConnection();
        var wanted = new[] { nodes[17].QualifiedName, nodes[4_321].QualifiedName };

        // A scan of the repository's 5,000 nodes reads thousands of rows; the digest seek reads a few.
        var before = await RowsReadAsync(connection);
        var single = await store.Value.FindNodeByQualifiedNameAsync(Repository, wanted[0]);
        var caseVariant = await store.Value.FindNodeByQualifiedNameAsync(Repository, wanted[0].ToUpperInvariant());
        var batch = await store.Value.FindNodesByQualifiedNamesAsync(Repository, [.. wanted, "missing", wanted[1].ToUpperInvariant()]);
        var parameters = new DynamicParameters(new { Repository });
        var rows = (await connection.QueryAsync<string>(
            $"SELECT qualified_name FROM nodes WHERE repository = @Repository AND {MySqlGraphStore.QualifiedNameMatch(wanted, parameters)}",
            parameters)).ToList();
        var rowsRead = await RowsReadAsync(connection) - before;

        single!.QualifiedName.ShouldBe(wanted[0]);
        caseVariant.ShouldBeNull();
        batch.Select(node => node.QualifiedName).ShouldBe(wanted, ignoreOrder: true);
        rows.ShouldBe(wanted, ignoreOrder: true);
        rowsRead.ShouldBeLessThan(100);
    }

    private static async Task<long> RowsReadAsync(MySqlConnection connection) =>
        (await connection.QueryAsync<(string Name, string Value)>(
            "SHOW SESSION STATUS WHERE Variable_name IN ('Handler_read_rnd_next', 'Handler_read_next')"))
        .Sum(row => long.Parse(row.Value));

    [Fact]
    public async Task SyncState_RoundTripsFailureTrackingAndSnapshotReplacementClearsIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = db.Store(new MariaDbStorageOptions());
        await using var _ = store.Context;
        await store.Value.UpsertRepositoryAsync(new RepositoryEntity { Name = Repository });
        var failedAt = new DateTime(2026, 10, 11, 12, 48, 46, DateTimeKind.Utc);

        await store.Value.UpsertSyncStateAsync(new SyncStateEntity
        {
            Repository = Repository, LastCommitSha = "a".PadRight(40, 'a'), Status = "error", ErrorMessage = "first",
            FailedCommitSha = "b".PadRight(40, 'b'), ConsecutiveFailures = 1, LastFailureAt = failedAt
        });
        await store.Value.UpsertSyncStateAsync(new SyncStateEntity
        {
            Repository = Repository, LastCommitSha = "a".PadRight(40, 'a'), Status = "error", ErrorMessage = "second",
            FailedCommitSha = "b".PadRight(40, 'b'), ConsecutiveFailures = 2, LastFailureAt = failedAt.AddMinutes(15)
        });

        var failed = (await db.Store(new MariaDbStorageOptions()).Value.GetSyncStateAsync(Repository))!;
        failed.Status.ShouldBe("error");
        failed.ErrorMessage.ShouldBe("second");
        failed.FailedCommitSha.ShouldBe("b".PadRight(40, 'b'));
        failed.ConsecutiveFailures.ShouldBe(2);
        failed.LastFailureAt.ShouldBe(failedAt.AddMinutes(15));

        await store.Value.ReplaceRepositoryGraphAsync(Repository, [], [], new Dictionary<string, string>(),
            new RepositoryEntity { Name = Repository },
            new SyncStateEntity { Repository = Repository, LastCommitSha = "c".PadRight(40, 'c'), Status = "idle" });

        var cleared = (await db.Store(new MariaDbStorageOptions()).Value.GetSyncStateAsync(Repository))!;
        cleared.Status.ShouldBe("idle");
        cleared.LastCommitSha.ShouldBe("c".PadRight(40, 'c'));
        cleared.FailedCommitSha.ShouldBeNull();
        cleared.ConsecutiveFailures.ShouldBe(0);
        cleared.LastFailureAt.ShouldBeNull();
    }

    [Fact]
    public async Task AcquireRepositoryIndexingLockAsync_WaitsBeyondTheConnectionDefaultCommandTimeout()
    {
        await using var db = await TestDatabase.CreateAsync();
        var holder = db.Store(new MariaDbStorageOptions());
        var waiter = db.Store(new MariaDbStorageOptions());
        await using var holderContext = holder.Context;
        await using var waiterContext = waiter.Context;

        var held = await holder.Value.AcquireRepositoryIndexingLockAsync(Repository);
        var waiting = waiter.Value.AcquireRepositoryIndexingLockAsync(Repository);
        await Task.Delay(TimeSpan.FromSeconds(3));
        waiting.IsCompleted.ShouldBeFalse();

        await held.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AcquireRepositoryIndexingLocksAsync_HoldsMultipleNamesOnOnePooledConnection()
    {
        await using var db = await TestDatabase.CreateAsync();
        var holder = db.StoreWithSingleConnection(new MariaDbStorageOptions());
        await using var context = holder.Context;
        var repositories = new[] { "lock-group-c", "lock-group-a", "lock-group-b", "lock-group-a" };
        var lease = await holder.Value.AcquireRepositoryIndexingLocksAsync(repositories).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var owners = new List<long>();
            foreach (var repository in repositories.Distinct())
            {
                var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repository)));
                owners.Add(await db.ScalarAsync<long>($"SELECT COALESCE(IS_USED_LOCK('{name}'), 0)"));
            }
            owners.ShouldAllBe(owner => owner > 0);
            owners.Distinct().Count().ShouldBe(1);
        }
        finally { await lease.DisposeAsync(); }
        foreach (var repository in repositories.Distinct())
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repository)));
            (await db.ScalarAsync<long>($"SELECT COALESCE(IS_USED_LOCK('{name}'), 0)")).ShouldBe(0);
            await using var single = await holder.Value.AcquireRepositoryIndexingLockAsync(repository).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static (List<GraphNode> Nodes, List<PendingEdge> Edges, Dictionary<string, string> Hashes) BuildGraph()
    {
        var nodes = Enumerable.Range(0, NodeCount).Select(i => new GraphNode
        {
            Repository = Repository,
            Label = NodeLabel.Method,
            Name = $"fn_{i}",
            QualifiedName = $"rust-analyzer cargo inference_{i % 20} 0.1.0 module_{i % 97}/Type{i % 31}#fn_{i}().",
            FilePath = FilePath(i % FileCount),
            StartLine = 1,
            EndLine = 2
        }).ToList();
        var random = new Random(25698);
        var pairs = new HashSet<(int, int)>();
        while (pairs.Count < EdgeCount)
            pairs.Add((random.Next(NodeCount), random.Next(NodeCount)));
        var edges = pairs
            .Select(pair => new PendingEdge(nodes[pair.Item1].QualifiedName, nodes[pair.Item2].QualifiedName, EdgeType.CALLS))
            .ToList();
        var hashes = Enumerable.Range(0, FileCount).ToDictionary(FilePath, i => $"hash-{i}");
        return (nodes, edges, hashes);

        static string FilePath(int file) => $"crates/c{file % 20}/src/file_{file}.rs";
    }

    private static Dictionary<string, string> Rehash(Dictionary<string, string> hashes) =>
        hashes.ToDictionary(hash => hash.Key, hash => hash.Value + "-new");

    private sealed record OwnedStore(MySqlGraphStore Value, CodeGraphDbContext Context);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _baseConnectionString;
        private readonly string _shortTimeoutConnectionString;
        private readonly string _migrationsPath =
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations");

        private TestDatabase(string baseConnectionString, string shortTimeoutConnectionString)
        {
            _baseConnectionString = baseConnectionString;
            _shortTimeoutConnectionString = shortTimeoutConnectionString;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
            {
                Database = $"cg_large_write_{Guid.NewGuid():N}"
            };
            var baseConnectionString = builder.ConnectionString;
            builder.DefaultCommandTimeout = 1;
            var database = new TestDatabase(baseConnectionString, builder.ConnectionString);
            await new MariaDbMigrationRunner(
                    Options.Create(new MariaDbStorageOptions
                    {
                        ConnectionString = baseConnectionString,
                        MigrationsPath = database._migrationsPath
                    }),
                    NullLogger<MariaDbMigrationRunner>.Instance)
                .ApplyConfiguredMigrationsAsync();
            return database;
        }

        /// <summary>A store whose connection default command timeout is 1 s.</summary>
        public OwnedStore Store(MariaDbStorageOptions options) => CreateStore(options, _shortTimeoutConnectionString);

        public OwnedStore StoreWithSingleConnection(MariaDbStorageOptions options) =>
            CreateStore(options, new MySqlConnectionStringBuilder(_shortTimeoutConnectionString)
            { MaximumPoolSize = 1 }.ConnectionString);

        /// <summary>
        /// As <see cref="Store"/>, but a timed-out command closes the socket instead of waiting for
        /// KILL QUERY — what happened in production, where the cascaded delete could not be
        /// interrupted within MySqlConnector's cancellation grace and the connection went Broken.
        /// </summary>
        public OwnedStore StoreBreakingOnTimeout(MariaDbStorageOptions options) =>
            CreateStore(options, new MySqlConnectionStringBuilder(_shortTimeoutConnectionString)
            {
                CancellationTimeout = -1
            }.ConnectionString);

        public async Task Seed(List<GraphNode> nodes, List<PendingEdge> edges, Dictionary<string, string> hashes)
        {
            var seeder = CreateStore(new MariaDbStorageOptions(), _baseConnectionString);
            await using var _ = seeder.Context;
            await seeder.Value.ReplaceRepositoryGraphAsync(
                Repository, nodes, edges, hashes, new RepositoryEntity { Name = Repository }, null);
        }

        /// <summary>Holds a row lock on the first node until <paramref name="hold"/> elapses; await the result to release.</summary>
        public async Task<Task> LockFirstNodeAsync(TimeSpan hold)
        {
            var connection = new MySqlConnection(_baseConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT id FROM nodes ORDER BY id LIMIT 1 FOR UPDATE", transaction: transaction));
            return Task.Run(async () =>
            {
                await Task.Delay(hold);
                await transaction.RollbackAsync();
                await connection.DisposeAsync();
            });
        }

        public async Task<long> GlobalDeleteStatementCountAsync() =>
            long.Parse(await ScalarAsync<string>(
                "SELECT VARIABLE_VALUE FROM information_schema.GLOBAL_STATUS WHERE VARIABLE_NAME = 'COM_DELETE'"));

        /// <summary>Edges whose source id is lower than that of the edge inserted just before them.</summary>
        public async Task<long> EdgeSourcesOutOfInsertOrderAsync() =>
            await ScalarAsync<long>("""
                SELECT COUNT(*) FROM (
                    SELECT source_id < LAG(source_id) OVER (ORDER BY id) AS out_of_order FROM edges
                ) ordered
                WHERE out_of_order
                """);

        public async Task<long> CountAsync(string table) =>
            await ScalarAsync<long>($"SELECT COUNT(*) FROM {table}");

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new MySqlConnection(_baseConnectionString);
            return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, commandTimeout: 300))
                   ?? throw new InvalidOperationException($"No value for {sql}");
        }

        private OwnedStore CreateStore(MariaDbStorageOptions options, string connectionString)
        {
            options.ConnectionString = connectionString;
            options.MigrationsPath = _migrationsPath;
            var accessor = Options.Create(options);
            var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(connectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options);
            var store = new MySqlGraphStore(context, accessor, NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context), new MySqlMetricsStore(context), new MySqlReviewStore(context),
                new MariaDbMigrationRunner(accessor, NullLogger<MariaDbMigrationRunner>.Instance));
            return new OwnedStore(store, context);
        }

        public async ValueTask DisposeAsync()
        {
            var builder = new MySqlConnectionStringBuilder(_baseConnectionString);
            var database = builder.Database;
            builder.Database = "";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            await connection.ExecuteAsync(new CommandDefinition($"DROP DATABASE IF EXISTS `{database}`", commandTimeout: 300));
        }
    }
}
