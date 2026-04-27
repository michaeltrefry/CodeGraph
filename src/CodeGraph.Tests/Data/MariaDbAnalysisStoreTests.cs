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

public class MariaDbAnalysisStoreTests
{
    [Fact]
    public void MySqlAnalysisStore_ImplementsStandaloneAnalysisContract()
    {
        typeof(IAnalysisStore).IsAssignableFrom(typeof(MySqlAnalysisStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlAnalysisStore_RoundTripsAnalysisBatchAndGraphContextWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_analysis_store_test_{Guid.NewGuid():N}";
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

            var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(options);
            var store = new MySqlAnalysisStore(context);
            var now = DateTime.UtcNow;

            context.Repositories.Add(new RepositoryEntity
            {
                Name = "CodeGraph",
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();

            await store.UpsertRepositorySummaryAsync("CodeGraph", "Summary", ConfidenceLevel.High, "abc", "model");
            (await store.GetRepositorySummaryAsync("CodeGraph"))!.Confidence.ShouldBe(ConfidenceLevel.High);

            await store.UpsertProjectAnalysisAsync("CodeGraph", new StoredProjectAnalysis(
                "CodeGraph",
                "CodeGraph.Api",
                "API summary",
                ConfidenceLevel.Medium,
                [new StoredEndpoint("/api/graph", "GET", "Graph", null, null)],
                [new StoredService("GraphService", "Graph service", "IGraphService", "singleton")],
                ["MariaDB"],
                ["nodes"],
                "model",
                now));

            var analysis = (await store.GetProjectAnalysesAsync("CodeGraph")).Single();
            analysis.Endpoints.Single().Route.ShouldBe("/api/graph");
            analysis.Services.Single().Name.ShouldBe("GraphService");

            var batchId = await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
            {
                Repo = "CodeGraph",
                ProviderBatchId = "provider-batch",
                ProviderName = "local",
                ExecutionMode = "local_parallel",
                IncludeAllSource = true,
                Status = "submitted",
                RequestCount = 1,
                SubmittedAt = now
            });

            await store.CreateBatchRequestsAsync(
            [
                new AnalysisBatchRequestEntity
                {
                    BatchId = batchId,
                    Sequence = 2,
                    CustomId = "custom-1",
                    NodeLabel = "Class",
                    RequestPayloadJson = "{}",
                    Status = "pending"
                }
            ]);

            (await store.GetPendingBatchesAsync("CodeGraph")).Single().IncludeAllSource.ShouldBeTrue();
            (await store.GetBatchByProviderBatchIdAsync("provider-batch"))!.ExecutionMode.ShouldBe("local_parallel");

            await store.UpdateBatchRequestStateAsync(batchId, "custom-1", "succeeded", 3, "ok", "model", now);
            var request = (await store.GetBatchRequestsAsync(batchId)).Single();
            request.AttemptCount.ShouldBe(3);
            request.ResponseText.ShouldBe("ok");

            await store.UpdateBatchStatusAsync(batchId, "completed", 1, now);
            (await store.GetLatestBatchAsync("CodeGraph"))!.Status.ShouldBe("completed");
            (await store.GetPendingBatchesAsync("CodeGraph")).ShouldBeEmpty();

            await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
            {
                NodeId = 10,
                Description = "Important class",
                Confidence = "high",
                ModelUsed = "model"
            });
            (await store.GetNodeAnalysisAsync(10))!.Description.ShouldBe("Important class");
            (await store.GetNodeAnalysesBatchAsync([10])).ShouldContainKey(10);

            context.Nodes.AddRange(
                new NodeEntity
                {
                    Id = 10,
                    Project = "CodeGraph",
                    Label = NodeLabel.Class.ToString(),
                    Name = "Widget",
                    QualifiedName = "Widget",
                    FilePath = "Widget.cs"
                },
                new NodeEntity
                {
                    Id = 11,
                    Project = "CodeGraph",
                    Label = NodeLabel.Method.ToString(),
                    Name = "Run",
                    QualifiedName = "Widget.Run",
                    FilePath = "Widget.cs"
                });
            await context.SaveChangesAsync();

            context.Edges.AddRange(
                new EdgeEntity
                {
                    Id = 100,
                    Project = "CodeGraph",
                    SourceId = 10,
                    TargetId = 11,
                    Type = "DEFINES"
                },
                new EdgeEntity
                {
                    Id = 101,
                    Project = "CodeGraph",
                    SourceId = 11,
                    TargetId = 10,
                    Type = "CALLS"
                });
            await context.SaveChangesAsync();

            (await store.GetClassNodesWithEdgesAsync("CodeGraph")).Single().Name.ShouldBe("Widget");
            (await store.GetChildNodesAsync(10)).Single().Name.ShouldBe("Run");
            (await store.GetInboundEdgesAsync(10)).Single().Type.ShouldBe("CALLS");
            (await store.GetOutboundEdgesAsync(11)).Single().Type.ShouldBe("CALLS");
            (await store.GetAllNodesByProjectAsync("CodeGraph")).Count.ShouldBe(2);
            (await store.GetAllEdgesByProjectAsync("CodeGraph")).Count.ShouldBe(2);
            (await store.GetEdgesForNodesAsync([10])).Count.ShouldBe(2);
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
