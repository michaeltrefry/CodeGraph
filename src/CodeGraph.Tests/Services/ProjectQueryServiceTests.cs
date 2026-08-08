using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
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
    public async Task ListSchemasAsync_QueryCountAndPageEnrichmentRemainBoundedAsFleetGrows()
    {
        var smallFleet = await QueryFleetAsync(32);
        var largeFleet = await QueryFleetAsync(512);

        smallFleet.QueryCount.ShouldBe(1);
        largeFleet.QueryCount.ShouldBe(smallFleet.QueryCount);
        smallFleet.PageEnrichmentCount.ShouldBe(10);
        largeFleet.PageEnrichmentCount.ShouldBe(smallFleet.PageEnrichmentCount);
        smallFleet.Response.TotalTables.ShouldBe(32);
        largeFleet.Response.TotalTables.ShouldBe(512);
        largeFleet.Response.Total.ShouldBe(512);
        largeFleet.Response.Items.Select(item => item.DatabaseName)
            .ShouldBe(Enumerable.Range(20, 10).Select(index => $"Database{index:D4}"));

        static async Task<(SchemaListResponse Response, int QueryCount, int PageEnrichmentCount)> QueryFleetAsync(int size)
        {
            var store = new InMemoryGraphStore();
            for (var index = size - 1; index >= 0; index--)
            {
                var project = $"db:sql-prod/Database{index:D4}";
                await store.UpsertRepositoryAsync(new RepositoryEntity
                {
                    Name = project,
                    SourceGroup = "sql-prod",
                    Language = "SQL"
                });
                store.AddNode(new GraphNode
                {
                    Project = project,
                    Label = NodeLabel.Table,
                    Name = $"Table{index:D4}",
                    QualifiedName = $"dbo.Table{index:D4}"
                });
            }

            var response = await CreateService(store).ListSchemasAsync(null, null, null, 3, 10);
            return (response, store.SchemaSearchQueryCount, store.SchemaPageEnrichmentCount);
        }
    }

    [Fact]
    public async Task ListSchemasAsync_UsesStableProjectTieBreakerAcrossPages()
    {
        var store = new InMemoryGraphStore();
        foreach (var name in new[] { "db:server/Shared-C", "db:server/Shared-A", "db:server/Shared-B" })
        {
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = name,
                SourceGroup = "server",
                Properties = JsonSerializer.Serialize(new { serverName = "server", databaseName = "Shared" })
            });
        }

        var service = CreateService(store);
        var firstPage = await service.ListSchemasAsync(null, null, null, 1, 2);
        var secondPage = await service.ListSchemasAsync(null, null, null, 2, 2);

        firstPage.Items.Select(item => item.Name).ShouldBe(["db:server/Shared-A", "db:server/Shared-B"]);
        secondPage.Items.Select(item => item.Name).ShouldBe(["db:server/Shared-C"]);
    }

    [Fact]
    public async Task GetSchemaCatalogAsync_MapsColumnsIndexesConstraintsForeignKeysAndProcedures()
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
                },
                ["constraints"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "CK_Orders_CustomerId",
                        ["constraint_type"] = "CHECK",
                        ["columns"] = new[] { "CustomerId" },
                        ["check_clause"] = "CustomerId > 0"
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
        orders.Constraints.Single().Name.ShouldBe("CK_Orders_CustomerId");
        orders.Constraints.Single().Columns.ShouldBe(["CustomerId"]);
        orders.ForeignKeys.Single().ReferencedTable.ShouldBe("Customers");
        catalog.Tables.Single(table => table.Name == "Customers").Constraints.Single().ConstraintType.ShouldBe("PRIMARY KEY");
        catalog.Procedures.Single().Parameters.Single().Name.ShouldBe("@CustomerId");
    }

    [Fact]
    public async Task GetSchemaCatalogAsync_MapsCurrentSchemaExtractorGraphShape()
    {
        const string project = "db:sql-prod:Orders";
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = project,
            SourceGroup = "sql-prod",
            Properties = JsonSerializer.Serialize(new { serverName = "sql-prod", databaseName = "Orders" })
        });

        var customersId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Table,
            Name = "customers",
            QualifiedName = "sql-prod.Orders.customers"
        });
        var ordersId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Table,
            Name = "orders",
            QualifiedName = "sql-prod.Orders.orders",
            Properties = new Dictionary<string, object>
            {
                ["primaryKeyColumns"] = new List<object> { "id" },
                ["indexes"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "uq_orders_external_id",
                        ["isUnique"] = true,
                        ["indexType"] = "BTREE",
                        ["columns"] = new List<string> { "external_id" }
                    }
                }
            }
        });
        var customerId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Column,
            Name = "id",
            QualifiedName = "sql-prod.Orders.customers.id",
            Properties = new Dictionary<string, object>
            {
                ["dataType"] = "bigint",
                ["ordinal"] = 1,
                ["nullable"] = false,
                ["isPrimaryKey"] = true
            }
        });
        var orderId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Column,
            Name = "id",
            QualifiedName = "sql-prod.Orders.orders.id",
            Properties = new Dictionary<string, object>
            {
                ["dataType"] = "bigint",
                ["ordinal"] = 1,
                ["nullable"] = false,
                ["isPrimaryKey"] = true
            }
        });
        var orderCustomerId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Column,
            Name = "customer_id",
            QualifiedName = "sql-prod.Orders.orders.customer_id",
            Properties = new Dictionary<string, object>
            {
                ["dataType"] = "bigint",
                ["ordinal"] = 2,
                ["nullable"] = false
            }
        });
        var orderExternalId = await store.UpsertNodeAsync(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Column,
            Name = "external_id",
            QualifiedName = "sql-prod.Orders.orders.external_id",
            Properties = new Dictionary<string, object>
            {
                ["dataType"] = "varchar(64)",
                ["ordinal"] = 3,
                ["nullable"] = false
            }
        });

        foreach (var edge in new[]
        {
            new GraphEdge { Project = project, SourceId = customersId, TargetId = customerId, Type = EdgeType.HAS_COLUMN },
            new GraphEdge { Project = project, SourceId = ordersId, TargetId = orderId, Type = EdgeType.HAS_COLUMN },
            new GraphEdge { Project = project, SourceId = ordersId, TargetId = orderCustomerId, Type = EdgeType.HAS_COLUMN },
            new GraphEdge { Project = project, SourceId = ordersId, TargetId = orderExternalId, Type = EdgeType.HAS_COLUMN },
            new GraphEdge
            {
                Project = project,
                SourceId = orderCustomerId,
                TargetId = customerId,
                Type = EdgeType.FOREIGN_KEY,
                Properties = new Dictionary<string, object>
                {
                    ["constraintName"] = "fk_orders_customer",
                    ["ordinal"] = 1
                }
            }
        })
        {
            await store.InsertEdgeAsync(edge);
        }

        var catalog = await CreateService(store).GetSchemaCatalogAsync(project);

        catalog.ShouldNotBeNull();
        var orders = catalog.Tables.Single(table => table.Name == "orders");
        orders.Columns.Select(column => column.Name).ShouldBe(["id", "customer_id", "external_id"]);
        orders.Columns[0].Id.ShouldBe(orderId);
        orders.PrimaryKeyColumns.ShouldBe(["id"]);
        var index = orders.Indexes.Single();
        index.Name.ShouldBe("uq_orders_external_id");
        index.IsUnique.ShouldBeTrue();
        index.IndexType.ShouldBe("BTREE");
        index.Columns.ShouldBe(["external_id"]);
        orders.Constraints.Single(constraint => constraint.ConstraintType == "PRIMARY KEY").Columns.ShouldBe(["id"]);
        var foreignKey = orders.ForeignKeys.Single();
        foreignKey.Name.ShouldBe("fk_orders_customer");
        foreignKey.Columns.ShouldBe(["customer_id"]);
        foreignKey.ReferencedTable.ShouldBe("customers");
        foreignKey.ReferencedColumns.ShouldBe(["id"]);
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
