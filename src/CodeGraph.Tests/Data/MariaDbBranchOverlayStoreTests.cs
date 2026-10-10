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

public class MariaDbBranchOverlayStoreTests
{
    [Fact]
    public async Task OverlayLifecycle_PersistsSliceSeparatelyAndTtlSweepDeletesIt_WhenConnectionIsConfigured()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString());
        var databaseName = $"codegraph_overlay_test_{Guid.NewGuid():N}";
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
            await using (var seed = new MySqlConnection(builder.ConnectionString))
                await seed.ExecuteAsync("INSERT INTO repositories (name) VALUES ('OverlayRepo')");

            var created = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            long oldId;
            long freshId;
            await using (var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlBranchOverlayStore(context);
                oldId = await store.CreateOverlayAsync(new BranchOverlayEntity
                {
                    Project = "OverlayRepo",
                    RequestedRef = "factory/feature",
                    RequestedByUsername = "michael",
                    CreatedAt = created
                });
                freshId = await store.CreateOverlayAsync(new BranchOverlayEntity
                {
                    Project = "OverlayRepo",
                    RequestedRef = "factory/fresh",
                    CreatedAt = created.AddHours(30)
                });
                await store.SetOverlayRunAsync(oldId, 42);
                (await store.MarkOverlayIndexingAsync(oldId, created.AddMinutes(1))).ShouldBeTrue();
                var indexing = await store.GetOverlayAsync(oldId);
                indexing!.Status.ShouldBe(BranchOverlayStatuses.Indexing);
                indexing.IndexerRunId.ShouldBe(42);

                var completed = await store.CompleteOverlayAsync(
                    oldId,
                    new string('a', 40),
                    new string('b', 40),
                    [
                        new BranchOverlayFileEntity { OverlayId = oldId, FilePath = "src/a.py", ChangeKind = BranchOverlayChangeKinds.Modified },
                        new BranchOverlayFileEntity { OverlayId = oldId, FilePath = "src/d.py", ChangeKind = BranchOverlayChangeKinds.Deleted },
                        new BranchOverlayFileEntity { OverlayId = oldId, FilePath = "src/f.py", ChangeKind = BranchOverlayChangeKinds.Renamed, PreviousPath = "src/d.py" },
                        // Case-only rename: tombstone and renamed row differ only by case.
                        new BranchOverlayFileEntity { OverlayId = oldId, FilePath = "src/Foo.py", ChangeKind = BranchOverlayChangeKinds.Deleted },
                        new BranchOverlayFileEntity { OverlayId = oldId, FilePath = "src/foo.py", ChangeKind = BranchOverlayChangeKinds.Renamed, PreviousPath = "src/Foo.py" }
                    ],
                    [
                        new GraphNode
                        {
                            Project = "OverlayRepo",
                            Label = NodeLabel.Function,
                            Name = "alpha",
                            QualifiedName = "OverlayRepo:src/a.py:alpha",
                            FilePath = "src/a.py",
                            StartLine = 3,
                            EndLine = 9,
                            Properties = new Dictionary<string, object> { ["visibility"] = "public" }
                        },
                        new GraphNode
                        {
                            Project = "OverlayRepo",
                            Label = NodeLabel.Function,
                            Name = "Alpha",
                            QualifiedName = "OverlayRepo:src/a.py:Alpha",
                            FilePath = "src/a.py"
                        }
                    ],
                    [new PendingEdge("OverlayRepo:src/a.py:alpha", "OverlayRepo:src/b.py:beta", EdgeType.CALLS)],
                    created.AddMinutes(2));
                completed.ShouldBeTrue();

                var ready = await store.GetOverlayAsync(oldId);
                ready!.Status.ShouldBe(BranchOverlayStatuses.Ready);
                ready.HeadSha.ShouldBe(new string('a', 40));
                ready.BaseSha.ShouldBe(new string('b', 40));
                ready.ChangedFileCount.ShouldBe(5);
                ready.NodeCount.ShouldBe(2);
                ready.EdgeCount.ShouldBe(1);

                var files = await store.GetOverlayFilesAsync(oldId);
                files.Single(file => file.FilePath == "src/f.py").PreviousPath.ShouldBe("src/d.py");
                files.Single(file => file.FilePath == "src/d.py").ChangeKind.ShouldBe(BranchOverlayChangeKinds.Deleted);
                files.Single(file => file.FilePath == "src/Foo.py").ChangeKind.ShouldBe(BranchOverlayChangeKinds.Deleted);
                files.Single(file => file.FilePath == "src/foo.py").PreviousPath.ShouldBe("src/Foo.py");
                var nodes = await store.GetOverlayNodesAsync(oldId);
                nodes.Select(node => node.QualifiedName).ShouldBe(
                    ["OverlayRepo:src/a.py:alpha", "OverlayRepo:src/a.py:Alpha"], ignoreOrder: true);
                nodes.Single(node => node.Name == "alpha").Properties["visibility"].ToString().ShouldBe("public");
                (await store.GetOverlayEdgesAsync(oldId)).Single().Type.ShouldBe(EdgeType.CALLS);

                // Lookup by commit: resolved heads, plus queued overlays requested by that SHA.
                var queuedBySha = await store.CreateOverlayAsync(new BranchOverlayEntity
                {
                    Project = "OverlayRepo",
                    RequestedRef = new string('a', 40),
                    CreatedAt = created.AddHours(31)
                });
                (await store.FindOverlaysByShaAsync(new string('a', 40), "OverlayRepo"))
                    .Select(overlay => overlay.Id).ShouldBe([queuedBySha, oldId]);
                (await store.FindOverlaysByShaAsync(new string('a', 40), null))
                    .Select(overlay => overlay.Id).ShouldBe([queuedBySha, oldId]);
                (await store.FindOverlaysByShaAsync(new string('a', 40), "OtherRepo")).ShouldBeEmpty();
                (await store.FindOverlaysByShaAsync(new string('f', 40), null)).ShouldBeEmpty();
            }

            await using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM nodes")).ShouldBe(0);
                (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM edges")).ShouldBe(0);
            }

            await using (var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString)))
            {
                var store = new MySqlBranchOverlayStore(context);
                var expiredIds = await store.ExpireOverlaysAsync(created.AddHours(24), created.AddHours(24));
                expiredIds.ShouldBe([oldId]);

                var expired = await store.GetOverlayAsync(oldId);
                expired!.Status.ShouldBe(BranchOverlayStatuses.Expired);
                expired.ExpiredAt.ShouldNotBeNull();
                (await store.GetOverlayFilesAsync(oldId)).ShouldBeEmpty();
                (await store.GetOverlayNodesAsync(oldId)).ShouldBeEmpty();
                (await store.GetOverlayEdgesAsync(oldId)).ShouldBeEmpty();
                (await store.GetOverlayAsync(freshId))!.Status.ShouldBe(BranchOverlayStatuses.Queued);

                // An expired overlay cannot be revived by a late indexing completion.
                (await store.MarkOverlayIndexingAsync(oldId, created.AddHours(25))).ShouldBeFalse();
                (await store.CompleteOverlayAsync(oldId, "c", "d", [], [], [], created.AddHours(25))).ShouldBeFalse();
                await store.MarkOverlayFailedAsync(oldId, "late", null, null, created.AddHours(25));
                (await store.GetOverlayAsync(oldId))!.Status.ShouldBe(BranchOverlayStatuses.Expired);
                (await store.ExpireOverlaysAsync(created.AddHours(24), created.AddHours(26))).ShouldBeEmpty();
            }
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
