using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using System.Text.Json;

namespace CodeGraph.Tests.Services;

public class ProjectQueryServiceTests
{
    [Fact]
    public async Task ListSchemasAsync_ReturnsOnlyDatabaseSchemaProjectsWithCounts()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "db:sql-prod/Orders",
            SourceGroup = "sql-prod",
            Language = "SQL",
            Framework = "MariaDB",
            Properties = JsonSerializer.Serialize(new { serverName = "sql-prod", databaseName = "Orders" })
        });
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "CodeGraph", Language = "C#" });
        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "db:sql-prod/Orders",
            Label = NodeLabel.Table,
            Name = "Customers",
            QualifiedName = "dbo.Customers"
        });
        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "db:sql-prod/Orders",
            Label = NodeLabel.View,
            Name = "ActiveCustomers",
            QualifiedName = "dbo.ActiveCustomers"
        });

        var service = CreateService(store);

        var result = await service.ListSchemasAsync(null, null, null, 1, 25);

        result.Total.ShouldBe(1);
        result.TotalTables.ShouldBe(1);
        result.TotalViews.ShouldBe(1);
        result.Items.Single().DatabaseName.ShouldBe("Orders");
        result.Servers.ShouldBe(["sql-prod"]);
    }

    [Fact]
    public async Task GetSchemaCatalogAsync_MapsColumnsIndexesForeignKeysAndProcedures()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "db:sql-prod/Orders",
            SourceGroup = "sql-prod",
            Properties = JsonSerializer.Serialize(new { serverName = "sql-prod", databaseName = "Orders" })
        });

        var customerId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "db:sql-prod/Orders",
            Label = NodeLabel.Table,
            Name = "Customers",
            QualifiedName = "dbo.Customers",
            Properties = new Dictionary<string, object>
            {
                ["columns"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "Id",
                        ["type"] = "INT",
                        ["nullable"] = false,
                        ["is_primary_key"] = true
                    }
                }
            }
        });
        var orderId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = "db:sql-prod/Orders",
            Label = NodeLabel.Table,
            Name = "Orders",
            QualifiedName = "dbo.Orders",
            Properties = new Dictionary<string, object>
            {
                ["columns"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "CustomerId",
                        ["type"] = "INT",
                        ["nullable"] = false
                    }
                }
            }
        });
        await store.UpsertNodeAsync(new GraphNode
        {
            Project = "db:sql-prod/Orders",
            Label = NodeLabel.StoredProcedure,
            Name = "GetOrders",
            QualifiedName = "dbo.GetOrders",
            Properties = new Dictionary<string, object>
            {
                ["parameters"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "@CustomerId",
                        ["type"] = "INT"
                    }
                }
            }
        });
        await store.InsertEdgeAsync(new GraphEdge
        {
            Project = "db:sql-prod/Orders",
            SourceId = orderId,
            TargetId = customerId,
            Type = EdgeType.QUERIES,
            Properties = new Dictionary<string, object>
            {
                ["relationship"] = "foreign_key",
                ["column"] = "CustomerId",
                ["referenced_column"] = "Id"
            }
        });
        await store.InsertEdgeAsync(new GraphEdge
        {
            Project = "db:sql-prod/Orders",
            SourceId = orderId,
            TargetId = orderId,
            Type = EdgeType.DEFINES,
            Properties = new Dictionary<string, object>
            {
                ["relationship"] = "index",
                ["index_name"] = "IX_Orders_CustomerId",
                ["columns"] = "CustomerId",
                ["is_unique"] = false
            }
        });

        var service = CreateService(store);

        var catalog = await service.GetSchemaCatalogAsync("db:sql-prod/Orders");

        catalog.ShouldNotBeNull();
        catalog.DatabaseName.ShouldBe("Orders");
        var orders = catalog.Tables.Single(table => table.Name == "Orders");
        orders.Columns.Single().Name.ShouldBe("CustomerId");
        orders.Indexes.Single().Name.ShouldBe("IX_Orders_CustomerId");
        orders.ForeignKeys.Single().ReferencedTable.ShouldBe("Customers");
        catalog.Procedures.Single().Parameters.Single().Name.ShouldBe("@CustomerId");
    }

    [Fact]
    public async Task GetHealthAsync_MapsRepositoryVitalityAndOrdersHotspotsByConcernScore()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "TestProject",
            LocalPath = "/tmp/testproject",
            Language = "C#",
            Framework = ".NET"
        });

        await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
        {
            Project = "TestProject",
            DotnetProject = null,
            OverallHealth = 6.8,
            TotalFiles = 4,
            HotspotCount = 2,
            AlertCount = 0,
            HistoryMaturity = "Growing",
            HasSufficientHistoryForTrends = true,
            ActivityStatus = "Slowing",
            FirefightingStatus = "Moderate",
            MonthlyCommitCounts = """[{"month":"2025-10","commitCount":8},{"month":"2025-11","commitCount":5}]""",
            VelocityLast6Months = 12,
            VelocityPrior6Months = 20,
            VelocityChangePercent = -40,
            DormantMonths12m = 1,
            MaxInactiveStreakMonths = 1,
            FirefightingCommits90d = 2,
            FirefightingCommits365d = 5,
            FirefightingRate90d = 0.18,
            FirefightingRate365d = 0.12,
            ComputedAt = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc)
        });

        await store.UpsertFileMetricsBatchAsync("TestProject",
        [
            new FileMetricsEntity
            {
                Project = "TestProject",
                FilePath = "src/LowRiskHighConcern.cs",
                DotnetProject = "TestProject.Api",
                HealthScore = 4.8,
                RiskScore = 8,
                ConcernScore = 22,
                BugFixCommits365d = 1.5,
                BugFixRatio365d = 0.6,
                RecurringChurnScore = 0.7,
                ComputedAt = DateTime.UtcNow
            },
            new FileMetricsEntity
            {
                Project = "TestProject",
                FilePath = "src/HighRiskLowConcern.cs",
                DotnetProject = "TestProject.Api",
                HealthScore = 3.5,
                RiskScore = 20,
                ConcernScore = 10,
                BugFixCommits365d = 0.1,
                BugFixRatio365d = 0.05,
                RecurringChurnScore = 0.1,
                ComputedAt = DateTime.UtcNow
            }
        ]);

        var service = CreateService(store);

        var response = await service.GetHealthAsync("TestProject");

        response.ShouldNotBeNull();
        response.RepositoryVitality.ShouldNotBeNull();
        response.RepositoryVitality.HistoryMaturity.ShouldBe(Models.Responses.HistoryMaturity.Growing);
        response.RepositoryVitality.ActivityStatus.ShouldBe("Slowing");
        response.RepositoryVitality.FirefightingStatus.ShouldBe("Moderate");
        response.RepositoryVitality.MonthlyCommits.Count.ShouldBe(2);
        response.TopHotspots.Count.ShouldBe(2);
        response.TopHotspots[0].FilePath.ShouldBe("src/LowRiskHighConcern.cs");
        response.TopHotspots[0].ConcernScore.ShouldBe(22);
    }

    private static ProjectQueryService CreateService(InMemoryGraphStore store) =>
        new(store, Options.Create(new RepositorySourceOptions()));
}
