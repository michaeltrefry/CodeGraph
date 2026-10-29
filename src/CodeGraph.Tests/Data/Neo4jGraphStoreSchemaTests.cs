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
    public async Task SearchSchemaRepositoriesAsync_ExecutesFourStatementsAtBoth32And512Schemas()
    {
        var uri = Require("CODEGRAPH_NEO4J_TEST_URI");

        var suffix = Guid.NewGuid().ToString("N");
        var server = $"scale-{suffix}";
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });
        var counter = new BoltSchemaStatementCounter();

        await Neo4jTestSchema.EnsureAppliedAsync(options);
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
            AssertSchemaScalePage(small, server);
            AssertSchemaScalePage(large, server);
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
    public async Task SearchSchemaRepositoriesAsync_ReturnsDeterministicPageAndFleetAggregates()
    {
        var uri = Require("CODEGRAPH_NEO4J_TEST_URI");

        var suffix = Guid.NewGuid().ToString("N");
        var server = $"server-{suffix}";
        var billingRepository = $"db:{server}/Billing";
        var ordersRepository = $"schema-{suffix}-orders";
        var whitespaceServerRepository = $"db:invalid-{suffix}/WhitespaceServer";
        var whitespaceDatabaseRepository = $"invalid-{suffix}-whitespace-database";
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });

        await Neo4jTestSchema.EnsureAppliedAsync(options);
        await using var factory = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(factory, options, NullLogger<Neo4jGraphStore>.Instance);

        try
        {
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = billingRepository,
                SourceGroup = server
            });
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = ordersRepository,
                Properties = $$"""{"serverName":"{{server}}","databaseName":"Orders"}"""
            });
            await SeedWhitespaceSchemasAsync(
                factory,
                server,
                whitespaceServerRepository,
                whitespaceDatabaseRepository);
            await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Repository = ordersRepository,
                    Label = NodeLabel.Table,
                    Name = "Customers",
                    QualifiedName = $"{suffix}.Customers"
                },
                new GraphNode
                {
                    Repository = ordersRepository,
                    Label = NodeLabel.StoredProcedure,
                    Name = "GetCustomers",
                    QualifiedName = $"{suffix}.GetCustomers"
                }
            ]);

            var allTestSchemas = await store.SearchSchemaRepositoriesAsync(search: suffix);
            var result = await store.SearchSchemaRepositoriesAsync(server: server, page: 2, pageSize: 1);

            allTestSchemas.TotalCount.ShouldBe(2);
            allTestSchemas.Items.Select(item => item.Repository.Name)
                .ShouldBe([billingRepository, ordersRepository]);
            result.TotalCount.ShouldBe(2);
            result.TotalTables.ShouldBe(1);
            result.TotalProcedures.ShouldBe(1);
            result.Items.Single().Repository.Name.ShouldBe(ordersRepository);
            result.Items.Single().TableCount.ShouldBe(1);
            result.Databases.ShouldBe(["Billing", "Orders"]);

            var filtered = await store.SearchSchemaRepositoriesAsync(
                search: "ORDER",
                server: server.ToUpperInvariant(),
                database: "orders");
            filtered.TotalCount.ShouldBe(1);
            filtered.TotalTables.ShouldBe(1);
            filtered.Items.Single().Repository.Name.ShouldBe(ordersRepository);
        }
        finally
        {
            await store.DeleteNodesByRepositoryAsync(billingRepository);
            await store.DeleteNodesByRepositoryAsync(ordersRepository);
            await store.DeleteRepositoryAsync(billingRepository);
            await store.DeleteRepositoryAsync(ordersRepository);
            await store.DeleteRepositoryAsync(whitespaceServerRepository);
            await store.DeleteRepositoryAsync(whitespaceDatabaseRepository);
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
                WITH index, 'db:' + $server + '/Database' + right('0000' + toString(index), 4) AS repository
                CREATE (r:RepositoryRecord {
                    name: repository,
                    sourceGroup: $server,
                    isDatabaseSchema: true,
                    schemaServerName: $server,
                    schemaDatabaseName: 'Database' + right('0000' + toString(index), 4),
                    schemaScaleTest: $suffix
                })
                CREATE (:CodeNode {
                    repository: repository,
                    label: 'Table',
                    name: 'Table' + right('0000' + toString(index), 4),
                    qualifiedName: 'dbo.Table' + right('0000' + toString(index), 4),
                    schemaScaleTest: $suffix
                })
                """, new { indexes, server, suffix });
        });
    }

    private static async Task SeedWhitespaceSchemasAsync(
        Neo4jSessionFactory factory,
        string server,
        string whitespaceServerRepository,
        string whitespaceDatabaseRepository)
    {
        await using var session = factory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (:RepositoryRecord {
                    name: $whitespaceServerRepository,
                    sourceGroup: $whitespaceServer
                })
                CREATE (:RepositoryRecord {
                    name: $whitespaceDatabaseRepository,
                    sourceGroup: $server,
                    properties: '{"databaseName":"   "}'
                })
                """, new
                {
                    whitespaceServerRepository,
                    whitespaceDatabaseRepository,
                    whitespaceServer = " \t ",
                    server
                });
        });
    }

    private static void AssertSchemaScalePage(SchemaRepositorySearchResult result, string server)
    {
        var expectedRepositories = Enumerable.Range(20, 10)
            .Select(index => $"db:{server}/Database{index:0000}")
            .ToArray();

        result.Items.Count.ShouldBe(10);
        result.Items.Select(item => item.Repository.Name).ShouldBe(expectedRepositories);
        result.Items.ShouldAllBe(item =>
            item.TableCount == 1 && item.ViewCount == 0 && item.ProcedureCount == 0);
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the Neo4j schema integration tests.");

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
