using Dapper;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Tests.Data;

/// <summary>
/// Integration tests for MySqlGraphStore against a real MySQL/MariaDB instance.
/// Set CODEGRAPH_TEST_MYSQL env var to a connection string, or uses default localhost root.
/// The test database is created/dropped per test class run.
/// </summary>
public class MySqlGraphStoreTests : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();

    static MySqlGraphStoreTests()
    {
        var envPath = Path.Combine(RepoRoot, ".env");
        if (File.Exists(envPath))
            Env.Load(envPath);
    }

    private MySqlGraphStore _store = null!;
    private CodeGraphDbContext _context = null!;
    private string _adminConnStr = null!;
    private string _testConnStr = null!;
    private const string TestDb = "codegraph_test";

    private static readonly string MigrationsPath = Path.Combine(
        RepoRoot, "sql", "migrations");

    public async Task InitializeAsync()
    {
        _adminConnStr = Environment.GetEnvironmentVariable("CODEGRAPH_TEST_MYSQL")
            ?? "Server=localhost;User=root;Password=;SslMode=None";

        // Drop and recreate test database
        using (var adminConn = new MySqlConnection(_adminConnStr))
        {
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS `{TestDb}`");
            await adminConn.ExecuteAsync($"CREATE DATABASE `{TestDb}`");
        }

        _testConnStr = new MySqlConnectionStringBuilder(_adminConnStr)
        {
            Database = TestDb
        }.ConnectionString;

        var serverVersion = ServerVersion.AutoDetect(_testConnStr);
        var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(_testConnStr, serverVersion)
            .Options;

        _context = new CodeGraphDbContext(dbOptions);

        var storageOptions = new CodeGraphStorageOptions
        {
            ConnectionString = _testConnStr,
            BatchSize = 100
        };

        _store = new MySqlGraphStore(_context, storageOptions, NullLogger<MySqlGraphStore>.Instance);
        await _store.ApplyMigrationsAsync(MigrationsPath);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try
        {
            using var adminConn = new MySqlConnection(_adminConnStr);
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS `{TestDb}`");
        }
        catch { }
    }

    // ── Project Tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertProject_InsertsAndUpdates()
    {
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = "TestProject", LocalPath = "/tmp/test" });

        var projects = await _store.ListRepositoriesAsync();
        var project = projects.ShouldContain(p => p.Name == "TestProject");
        project.LocalPath.ShouldBe("/tmp/test");

        // Update
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = "TestProject", RepoUrl = "https://git.example.com/test" });
        projects = await _store.ListRepositoriesAsync();
        project = projects.ShouldContain(p => p.Name == "TestProject");
        project.RepoUrl.ShouldBe("https://git.example.com/test");
        project.LocalPath.ShouldBe("/tmp/test"); // preserved
    }

    [Fact]
    public async Task DeleteProject_CascadesEverything()
    {
        var project = "CascadeTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project, LocalPath = "/tmp/cascade" });

        var nodeA = MakeNode(project, "ClassA", NodeLabel.Class);
        var nodeB = MakeNode(project, "ClassB", NodeLabel.Class);
        var ids = await _store.UpsertNodeBatchAsync([nodeA, nodeB]);

        await _store.InsertEdgeAsync(new GraphEdge
        {
            Project = project,
            SourceId = ids[nodeA.QualifiedName],
            TargetId = ids[nodeB.QualifiedName],
            Type = EdgeType.CALLS
        });

        await _store.UpsertFileHashBatchAsync(project, new Dictionary<string, string>
        {
            ["file.cs"] = "abc123"
        });

        await _store.DeleteRepositoryAsync(project);

        var nodes = await _store.FindNodesByLabelAsync(project, NodeLabel.Class);
        nodes.ShouldBeEmpty();

        var hashes = await _store.GetFileHashesAsync(project);
        hashes.ShouldBeEmpty();
    }

    // ── Node Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertNode_InsertsNewNode()
    {
        var project = "NodeTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var node = MakeNode(project, "MyClass", NodeLabel.Class);
        var id = await _store.UpsertNodeAsync(node);

        id.ShouldBeGreaterThan(0);

        var found = await _store.FindNodeByQualifiedNameAsync(project, node.QualifiedName);
        found.ShouldNotBeNull();
        found.Name.ShouldBe("MyClass");
        found.Label.ShouldBe(NodeLabel.Class);
    }

    [Fact]
    public async Task UpsertNode_UpdatesExistingNode()
    {
        var project = "NodeUpdateTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var node = MakeNode(project, "MyClass", NodeLabel.Class, startLine: 10);
        await _store.UpsertNodeAsync(node);

        var updated = node with { StartLine = 20, EndLine = 50 };
        await _store.UpsertNodeAsync(updated);

        var found = await _store.FindNodeByQualifiedNameAsync(project, node.QualifiedName);
        found.ShouldNotBeNull();
        found.StartLine.ShouldBe(20);
        found.EndLine.ShouldBe(50);
    }

    [Fact]
    public async Task UpsertNodeBatch_ReturnsQualifiedNameToIdMapping()
    {
        var project = "BatchNodeTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var nodes = Enumerable.Range(0, 100)
            .Select(i => MakeNode(project, $"Class{i}", NodeLabel.Class))
            .ToList();

        var ids = await _store.UpsertNodeBatchAsync(nodes);

        ids.Count.ShouldBe(100);
        foreach (var node in nodes)
            ids.ShouldContainKey(node.QualifiedName);
    }

    [Fact]
    public async Task FindNodesByName_ReturnsMatchingNodes()
    {
        var project = "FindByNameTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        await _store.UpsertNodeBatchAsync([
            MakeNode(project, "OrderService", NodeLabel.Class),
            MakeNode(project, "OrderService", NodeLabel.Interface, "IOrderService"),
            MakeNode(project, "PaymentService", NodeLabel.Class),
        ]);

        var found = await _store.FindNodesByNameAsync(project, "OrderService");
        found.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SearchNodes_MatchesPattern()
    {
        var project = "SearchTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        await _store.UpsertNodeBatchAsync([
            MakeNode(project, "OrderService", NodeLabel.Class),
            MakeNode(project, "OrderController", NodeLabel.Class),
            MakeNode(project, "PaymentService", NodeLabel.Class),
        ]);

        var found = await _store.SearchNodesAsync(project, "Order");
        found.Count.ShouldBe(2);

        found = await _store.SearchNodesAsync(project, "Service", label: NodeLabel.Class);
        found.Count.ShouldBe(2);
    }

    // ── Edge Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertEdgeBatch_CreatesEdges()
    {
        var project = "EdgeBatchTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var nodeA = MakeNode(project, "ClassA", NodeLabel.Class);
        var nodeB = MakeNode(project, "ClassB", NodeLabel.Class);
        var nodeC = MakeNode(project, "ClassC", NodeLabel.Class);
        var ids = await _store.UpsertNodeBatchAsync([nodeA, nodeB, nodeC]);

        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Project = project, SourceId = ids[nodeA.QualifiedName], TargetId = ids[nodeB.QualifiedName], Type = EdgeType.CALLS },
            new GraphEdge { Project = project, SourceId = ids[nodeB.QualifiedName], TargetId = ids[nodeC.QualifiedName], Type = EdgeType.CALLS },
        ]);

        var edgesFromA = await _store.FindEdgesBySourceAsync(ids[nodeA.QualifiedName], EdgeType.CALLS);
        edgesFromA.Count.ShouldBe(1);
        edgesFromA[0].TargetId.ShouldBe(ids[nodeB.QualifiedName]);

        var edgesToC = await _store.FindEdgesByTargetAsync(ids[nodeC.QualifiedName], EdgeType.CALLS);
        edgesToC.Count.ShouldBe(1);
    }

    // ── Traversal Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task Traverse_Outbound_FollowsCallChain()
    {
        var project = "TraverseOutTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var nodes = new[] { "A", "B", "C", "D" }
            .Select(n => MakeNode(project, $"Class{n}", NodeLabel.Class))
            .ToList();
        var ids = await _store.UpsertNodeBatchAsync(nodes);

        var edges = new List<GraphEdge>();
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new GraphEdge
            {
                Project = project,
                SourceId = ids[nodes[i].QualifiedName],
                TargetId = ids[nodes[i + 1].QualifiedName],
                Type = EdgeType.CALLS
            });
        }
        await _store.InsertEdgeBatchAsync(edges);

        var result = await _store.TraverseAsync(
            ids[nodes[0].QualifiedName],
            TraceDirection.Outbound,
            maxDepth: 3);

        result.Count.ShouldBe(3);
        result.ShouldContain(r => r.Node.Name == "ClassB" && r.Depth == 1);
        result.ShouldContain(r => r.Node.Name == "ClassC" && r.Depth == 2);
        result.ShouldContain(r => r.Node.Name == "ClassD" && r.Depth == 3);
    }

    [Fact]
    public async Task Traverse_Inbound_FindsCallers()
    {
        var project = "TraverseInTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var nodeA = MakeNode(project, "CallerA", NodeLabel.Method);
        var nodeB = MakeNode(project, "CallerB", NodeLabel.Method);
        var nodeC = MakeNode(project, "Target", NodeLabel.Method);
        var ids = await _store.UpsertNodeBatchAsync([nodeA, nodeB, nodeC]);

        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Project = project, SourceId = ids[nodeA.QualifiedName], TargetId = ids[nodeC.QualifiedName], Type = EdgeType.CALLS },
            new GraphEdge { Project = project, SourceId = ids[nodeB.QualifiedName], TargetId = ids[nodeC.QualifiedName], Type = EdgeType.CALLS },
        ]);

        var result = await _store.TraverseAsync(
            ids[nodeC.QualifiedName],
            TraceDirection.Inbound,
            maxDepth: 1);

        result.Count.ShouldBe(2);
        result.ShouldContain(r => r.Node.Name == "CallerA");
        result.ShouldContain(r => r.Node.Name == "CallerB");
    }

    [Fact]
    public async Task Traverse_WithEdgeFilter_OnlyFollowsSpecifiedTypes()
    {
        var project = "TraverseFilterTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var nodeA = MakeNode(project, "ClassA", NodeLabel.Class);
        var nodeB = MakeNode(project, "ClassB", NodeLabel.Class);
        var nodeC = MakeNode(project, "InterfaceC", NodeLabel.Interface);
        var ids = await _store.UpsertNodeBatchAsync([nodeA, nodeB, nodeC]);

        await _store.InsertEdgeBatchAsync([
            new GraphEdge { Project = project, SourceId = ids[nodeA.QualifiedName], TargetId = ids[nodeB.QualifiedName], Type = EdgeType.CALLS },
            new GraphEdge { Project = project, SourceId = ids[nodeA.QualifiedName], TargetId = ids[nodeC.QualifiedName], Type = EdgeType.IMPLEMENTS },
        ]);

        var result = await _store.TraverseAsync(
            ids[nodeA.QualifiedName],
            TraceDirection.Outbound,
            maxDepth: 1,
            edgeFilter: [EdgeType.CALLS]);

        result.Count.ShouldBe(1);
        result[0].Node.Name.ShouldBe("ClassB");
    }

    // ── File Hash Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task FileHashes_IncrementalTracking()
    {
        var project = "HashTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        var hashes = new Dictionary<string, string>
        {
            ["src/OrderService.cs"] = "aaa111",
            ["src/PaymentService.cs"] = "bbb222"
        };

        await _store.UpsertFileHashBatchAsync(project, hashes);
        var loaded = await _store.GetFileHashesAsync(project);
        loaded.Count.ShouldBe(2);
        loaded["src/OrderService.cs"].ShouldBe("aaa111");

        // Update one hash
        await _store.UpsertFileHashBatchAsync(project, new Dictionary<string, string>
        {
            ["src/OrderService.cs"] = "ccc333"
        });
        loaded = await _store.GetFileHashesAsync(project);
        loaded["src/OrderService.cs"].ShouldBe("ccc333");
        loaded["src/PaymentService.cs"].ShouldBe("bbb222");

        // Delete
        await _store.DeleteFileHashesAsync(project, ["src/PaymentService.cs"]);
        loaded = await _store.GetFileHashesAsync(project);
        loaded.Count.ShouldBe(1);
    }

    // ── Summary Tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectSummary_UpsertAndRetrieve()
    {
        var project = "SummaryTest";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });

        await _store.UpsertRepositorySummaryAsync(project,
            "This service handles order processing.", ConfidenceLevel.High, "hash123");

        var summary = await _store.GetRepositorySummaryAsync(project);
        summary.ShouldNotBeNull();
        summary.Summary.ShouldBe("This service handles order processing.");
        summary.Confidence.ShouldBe(ConfidenceLevel.High);

        // Update
        await _store.UpsertRepositorySummaryAsync(project,
            "Updated summary.", ConfidenceLevel.Medium, "hash456");
        summary = await _store.GetRepositorySummaryAsync(project);
        summary.ShouldNotBeNull();
        summary.Summary.ShouldBe("Updated summary.");
        summary.Confidence.ShouldBe(ConfidenceLevel.Medium);
    }

    // ── Cross-Repo Edge Tests ─────────────────────────────────────────────

    [Fact]
    public async Task CrossRepoEdges_InsertAndQuery()
    {
        var projA = "CrossRepoA";
        var projB = "CrossRepoB";
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = projA });
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = projB });

        var nodeA = MakeNode(projA, "PublisherService", NodeLabel.Class);
        var nodeB = MakeNode(projB, "ConsumerService", NodeLabel.Class);
        var idsA = await _store.UpsertNodeBatchAsync([nodeA]);
        var idsB = await _store.UpsertNodeBatchAsync([nodeB]);

        await _store.InsertCrossRepoEdgeAsync(new CrossRepoEdge
        {
            SourceProject = projA,
            TargetProject = projB,
            SourceNodeId = idsA[nodeA.QualifiedName],
            TargetNodeId = idsB[nodeB.QualifiedName],
            Type = EdgeType.PUBLISHES
        });

        var edges = await _store.FindCrossRepoEdgesAsync(projA, EdgeType.PUBLISHES);
        edges.Count.ShouldBe(1);
        edges[0].TargetProject.ShouldBe(projB);

        // Also findable from target project
        edges = await _store.FindCrossRepoEdgesAsync(projB);
        edges.Count.ShouldBe(1);
    }

    // ── Migration Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task ApplyMigrations_IsIdempotent()
    {
        await _store.ApplyMigrationsAsync(MigrationsPath);

        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = "MigrationIdempotentTest" });
        var projects = await _store.ListRepositoriesAsync();
        projects.ShouldContain(p => p.Name == "MigrationIdempotentTest");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static GraphNode MakeNode(string project, string name, NodeLabel label,
        string? qualifiedName = null, int startLine = 0) => new()
    {
        Project = project,
        Name = name,
        Label = label,
        QualifiedName = qualifiedName ?? $"{project}.{name}",
        FilePath = $"src/{name}.cs",
        StartLine = startLine,
        EndLine = startLine + 10
    };

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null &&
               !Directory.Exists(Path.Combine(dir, ".git")) &&
               !File.Exists(Path.Combine(dir, ".env")) &&
               !File.Exists(Path.Combine(dir, "TC.CodeGraphApi.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Cannot find repo root");
    }
}

/// <summary>
/// Extension to make Shouldly work with ShouldContain returning the matched item.
/// </summary>
internal static class ShouldlyListExtensions
{
    public static T ShouldContain<T>(this IReadOnlyList<T> source, Func<T, bool> predicate) where T : class
    {
        var item = source.FirstOrDefault(predicate);
        item.ShouldNotBeNull("Expected collection to contain a matching item but none was found.");
        return item;
    }
}
