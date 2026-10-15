using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>Branch overlay answers over the MariaDB graph and overlay stores.</summary>
public class MariaDbOverlayQueryTests
{
    private const string Repo = "OverlayQueryRepo";

    [Fact]
    public async Task OverlayView_OverMariaDbStores_AnswersHeadGraph_WhenConnectionIsConfigured()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString());
        var databaseName = $"cg_overlay_query_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var baseSha = new string('b', 40);
        var headSha = new string('a', 40);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;
            await using var context = new CodeGraphDbContext(dbOptions);
            var store = new MySqlGraphStore(
                context,
                storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context),
                new MySqlMetricsStore(context),
                new MySqlReviewStore(context),
                runner);
            var overlays = new MySqlBranchOverlayStore(context);

            await store.ReplaceProjectGraphAsync(
                Repo,
                [
                    Function("alpha", "src/a.py"),
                    Function("gone", "src/a.py"),
                    Function("beta", "src/b.py"),
                    Function("delta", "src/d.py")
                ],
                [
                    new PendingEdge(Qn("src/b.py", "beta"), Qn("src/a.py", "alpha"), EdgeType.CALLS),
                    new PendingEdge(Qn("src/b.py", "beta"), Qn("src/a.py", "gone"), EdgeType.CALLS),
                    new PendingEdge(Qn("src/b.py", "beta"), Qn("src/d.py", "delta"), EdgeType.CALLS)
                ],
                new Dictionary<string, string>(),
                new RepositoryEntity { Name = Repo, LastCommitSha = baseSha },
                syncState: null);

            var created = DateTime.UtcNow;
            var overlayId = await overlays.CreateOverlayAsync(new BranchOverlayEntity
            {
                Project = Repo,
                RequestedRef = "factory/feature",
                CreatedAt = created
            });
            (await overlays.CompleteOverlayAsync(
                overlayId,
                headSha,
                baseSha,
                [
                    new BranchOverlayFileEntity { OverlayId = overlayId, FilePath = "src/a.py", ChangeKind = BranchOverlayChangeKinds.Modified },
                    new BranchOverlayFileEntity { OverlayId = overlayId, FilePath = "src/d.py", ChangeKind = BranchOverlayChangeKinds.Deleted },
                    new BranchOverlayFileEntity { OverlayId = overlayId, FilePath = "src/n.py", ChangeKind = BranchOverlayChangeKinds.Added }
                ],
                [Function("alpha", "src/a.py", 6), Function("newbie", "src/n.py")],
                [new PendingEdge(Qn("src/n.py", "newbie"), Qn("src/a.py", "alpha"), EdgeType.CALLS)],
                created)).ShouldBeTrue();

            var resolver = new CommitScopeResolver(
                store,
                Options.Create(new RepositorySourceOptions()),
                overlays,
                new BranchOverlayGit(),
                Options.Create(new BranchOverlayOptions()));
            var scope = await resolver.ResolveAsync(Repo, headSha.ToUpperInvariant());
            scope.NotIndexed.ShouldBeNull();
            scope.Sha.ShouldBe(headSha);

            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var head = await impact.AnalyzeFileImpactAsync(Repo, "src/a.py", graph: scope.Graph);
            head!.ChangedNodes.ShouldHaveSingleItem().Name.ShouldBe("alpha");
            head.ChangedNodes[0].NodeId.ShouldBeLessThan(0);
            head.AffectedNodes.Select(node => node.Name).ShouldBe(["beta", "newbie"], ignoreOrder: true);

            var defaultBranch = await impact.AnalyzeFileImpactAsync(Repo, "src/a.py");
            defaultBranch!.ChangedNodes.Select(node => node.Name).ShouldBe(["alpha", "gone"], ignoreOrder: true);
            defaultBranch.AffectedNodes.ShouldHaveSingleItem().Name.ShouldBe("beta");

            (await scope.Graph.FindNodesByFileAsync(Repo, "src/d.py")).ShouldBeEmpty();
            (await scope.Graph.SearchNodesAsync(Repo, "%", limit: 100)).Select(node => node.Name)
                .ShouldBe(["alpha", "beta", "newbie"], ignoreOrder: true);
            (await scope.Graph.SearchNodesCountAsync(Repo, "%")).ShouldBe(3);
            var beta = await store.FindNodeByQualifiedNameAsync(Repo, Qn("src/b.py", "beta"));
            (await scope.Graph.FindEdgesBySourceAsync(beta!.Id)).ShouldHaveSingleItem().TargetId.ShouldBeLessThan(0);

            // The default-branch graph is untouched.
            await using var conn = new MySqlConnection(builder.ConnectionString);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM nodes")).ShouldBe(4);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM edges")).ShouldBe(3);
        }
        finally
        {
            var drop = new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" };
            await using var conn = new MySqlConnection(drop.ConnectionString);
            await conn.OpenAsync();
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
        }
    }

    private static string Qn(string path, string name) => $"{Repo}:{path}:{name}";

    private static GraphNode Function(string name, string path, int startLine = 1) => new()
    {
        Project = Repo,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = Qn(path, name),
        FilePath = path,
        StartLine = startLine,
        EndLine = startLine + 1
    };
}
