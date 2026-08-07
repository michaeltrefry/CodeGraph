using System.Collections.Concurrent;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jGraphStoreSchemaTests
{
    [Fact]
    public async Task SearchSchemaRepositoriesAsync_ExecutesFourStatementsAtBoth32And512SchemasWhenConfigured()
    {
        var uri = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_URI");
        if (string.IsNullOrWhiteSpace(uri))
            return;

        var suffix = Guid.NewGuid().ToString("N");
        var server = $"scale-{suffix}";
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_USERNAME") ?? "neo4j",
            Neo4jPassword = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_PASSWORD") ?? "testpassword"
        });
        var counter = new BoltSchemaStatementCounter();

        await using var factory = new Neo4jSessionFactory(options, counter);
        var store = new Neo4jGraphStore(factory, options, NullLogger<Neo4jGraphStore>.Instance);

        try
        {
            await SeedSchemasAsync(factory, server, 0, 31);
            counter.Reset();
            var small = await store.SearchSchemaRepositoriesAsync(server: server, page: 3, pageSize: 10);
            var smallStatementCount = counter.SchemaStatementCount;

            await SeedSchemasAsync(factory, server, 32, 511);
            counter.Reset();
            var large = await store.SearchSchemaRepositoriesAsync(server: server, page: 3, pageSize: 10);
            var largeStatementCount = counter.SchemaStatementCount;

            small.TotalCount.ShouldBe(32);
            small.TotalTables.ShouldBe(32);
            large.TotalCount.ShouldBe(512);
            large.TotalTables.ShouldBe(512);
            smallStatementCount.ShouldBe(4, counter.DescribeDebugEvents());
            largeStatementCount.ShouldBe(smallStatementCount, counter.DescribeDebugEvents());
        }
        finally
        {
            await using var session = factory.GetSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("MATCH (n) WHERE n.schemaScaleTest = $suffix DETACH DELETE n", new { suffix });
            });
        }
    }

    [Fact]
    public async Task SearchSchemaRepositoriesAsync_ReturnsDeterministicPageAndFleetAggregatesWhenConfigured()
    {
        var uri = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_URI");
        if (string.IsNullOrWhiteSpace(uri))
            return;

        var suffix = Guid.NewGuid().ToString("N");
        var server = $"server-{suffix}";
        var billingProject = $"db:{server}/Billing";
        var ordersProject = $"schema-{suffix}-orders";
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_USERNAME") ?? "neo4j",
            Neo4jPassword = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_PASSWORD") ?? "testpassword"
        });

        await using var factory = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(factory, options, NullLogger<Neo4jGraphStore>.Instance);

        try
        {
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = billingProject,
                SourceGroup = server
            });
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = ordersProject,
                Properties = $$"""{"serverName":"{{server}}","databaseName":"Orders"}"""
            });
            await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = ordersProject,
                    Label = NodeLabel.Table,
                    Name = "Customers",
                    QualifiedName = $"{suffix}.Customers"
                },
                new GraphNode
                {
                    Project = ordersProject,
                    Label = NodeLabel.StoredProcedure,
                    Name = "GetCustomers",
                    QualifiedName = $"{suffix}.GetCustomers"
                }
            ]);

            var result = await store.SearchSchemaRepositoriesAsync(server: server, page: 2, pageSize: 1);

            result.TotalCount.ShouldBe(2);
            result.TotalTables.ShouldBe(1);
            result.TotalProcedures.ShouldBe(1);
            result.Items.Single().Project.Name.ShouldBe(ordersProject);
            result.Items.Single().TableCount.ShouldBe(1);
            result.Databases.ShouldBe(["Billing", "Orders"]);

            var filtered = await store.SearchSchemaRepositoriesAsync(
                search: "ORDER",
                server: server.ToUpperInvariant(),
                database: "orders");
            filtered.TotalCount.ShouldBe(1);
            filtered.TotalTables.ShouldBe(1);
            filtered.Items.Single().Project.Name.ShouldBe(ordersProject);
        }
        finally
        {
            await store.DeleteNodesByProjectAsync(billingProject);
            await store.DeleteNodesByProjectAsync(ordersProject);
            await store.DeleteRepositoryAsync(billingProject);
            await store.DeleteRepositoryAsync(ordersProject);
        }
    }

    private static async Task SeedSchemasAsync(
        Neo4jSessionFactory factory,
        string server,
        int first,
        int last)
    {
        var suffix = server["scale-".Length..];
        var indexes = Enumerable.Range(first, last - first + 1).ToArray();
        await using var session = factory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                UNWIND $indexes AS index
                WITH index, 'db:' + $server + '/Database' + right('0000' + toString(index), 4) AS project
                CREATE (r:RepositoryRecord {
                    name: project,
                    sourceGroup: $server,
                    isDatabaseSchema: true,
                    schemaServerName: $server,
                    schemaDatabaseName: 'Database' + right('0000' + toString(index), 4),
                    schemaScaleTest: $suffix
                })
                CREATE (:CodeNode {
                    project: project,
                    label: 'Table',
                    name: 'Table' + right('0000' + toString(index), 4),
                    qualifiedName: 'dbo.Table' + right('0000' + toString(index), 4),
                    schemaScaleTest: $suffix
                })
                """, new { indexes, server, suffix });
        });
    }

    private sealed class BoltSchemaStatementCounter : Neo4j.Driver.ILogger
    {
        private readonly ConcurrentQueue<string> debugEvents = new();

        public bool IsTraceEnabled() => false;
        public bool IsDebugEnabled() => true;
        public void Error(Exception cause, string message, params object[] args) { }
        public void Warn(Exception cause, string message, params object[] args) { }
        public void Info(string message, params object[] args) { }
        public void Trace(string message, params object[] args) { }

        public void Debug(string message, params object[] args)
        {
            var rendered = $"{message} | {string.Join(" | ", args.Select(arg => arg?.ToString()))}";
            debugEvents.Enqueue(rendered);
        }

        public int SchemaStatementCount => debugEvents.Count(entry =>
            entry.Contains("RUN", StringComparison.Ordinal)
            && entry.Contains("MATCH (r:RepositoryRecord)", StringComparison.Ordinal));

        public void Reset()
        {
            while (debugEvents.TryDequeue(out _))
            {
            }
        }

        public string DescribeDebugEvents() => string.Join(Environment.NewLine, debugEvents);
    }
}
