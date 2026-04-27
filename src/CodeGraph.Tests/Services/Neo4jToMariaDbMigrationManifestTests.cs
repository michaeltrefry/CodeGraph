using CodeGraph.Data;
using CodeGraph.Data.Migration;
using CodeGraph.Models;
using CodeGraph.Tests.Extractors;
using CodeGraph.Services.Migration;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class Neo4jToMariaDbMigrationManifestTests
{
    [Fact]
    public void CurrentManifest_CoversRequiredStandaloneMigrationAreasInOrder()
    {
        var manifest = Neo4jToMariaDbMigrationManifest.Current;

        manifest.Areas.Select(area => area.Key).ShouldBe([
            "repositories",
            "graph",
            "wiki",
            "analysis",
            "reviews",
            "metrics",
            "vectors",
            "memory",
            "assistant",
            "jobs"
        ]);
        var orders = manifest.Areas.Select(area => area.Order).ToList();
        orders.ShouldBe(orders.OrderBy(order => order).ToList());
        manifest.Areas.Single(area => area.Key == "memory").Description.ShouldContain("claim", Case.Insensitive);
    }

    [Fact]
    public void Planner_CreatesDryRunReportFromCurrentManifest()
    {
        var generatedAt = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);
        var planner = new Neo4jToMariaDbMigrationPlanner();

        var report = planner.CreateDryRunReport(generatedAtUtc: generatedAt);

        report.DryRun.ShouldBeTrue();
        report.GeneratedAtUtc.ShouldBe(generatedAt);
        report.TotalAreas.ShouldBe(Neo4jToMariaDbMigrationManifest.Current.Areas.Count);
        report.CanExecute.ShouldBeFalse();
        report.BlockedAreas.ShouldBe(report.TotalAreas);
        report.Steps.Select(step => step.Sequence).ShouldBe(Enumerable.Range(1, report.TotalAreas));
        report.Steps.Select(step => step.Key).ShouldBe(Neo4jToMariaDbMigrationManifest.Current.Areas.Select(area => area.Key));
        report.Steps.ShouldAllBe(step => step.Status == Neo4jToMariaDbMigrationPlanStatuses.Planned);
        report.Steps.ShouldAllBe(step => step.Notes.Contains("exporter", StringComparison.OrdinalIgnoreCase));
        report.Steps.ShouldAllBe(step => step.ExporterKey.StartsWith("neo4j:", StringComparison.Ordinal));
        report.Steps.ShouldAllBe(step => step.ImporterKey.StartsWith("mariadb:", StringComparison.Ordinal));
        report.Steps.ShouldAllBe(step => step.BlockingReason != null);
    }

    [Fact]
    public async Task Service_ExposesDryRunReport()
    {
        var service = new Neo4jToMariaDbMigrationService(new Neo4jToMariaDbMigrationPlanner());

        var report = await service.CreateDryRunReportAsync(new DateTime(2026, 4, 27, 1, 0, 0, DateTimeKind.Utc));

        report.DryRun.ShouldBeTrue();
        report.TotalAreas.ShouldBe(10);
    }

    [Fact]
    public async Task Service_DryRunIncludesCountsForImplementedRepositoriesAndGraphSlice()
    {
        var service = new Neo4jToMariaDbMigrationService(
            new Neo4jToMariaDbMigrationPlanner(),
            new FakeGraphExporter(new Neo4jToMariaDbGraphExport(
                [new RepositoryEntity { Name = "Demo" }],
                [Node(100, "Demo", NodeLabel.Class, "Demo.Customer")],
                [Edge(200, "Demo", 100, 101, EdgeType.CALLS)],
                [])),
            new InMemoryGraphStore());

        var report = await service.CreateDryRunReportAsync(new DateTime(2026, 4, 27, 1, 0, 0, DateTimeKind.Utc));

        var repositories = report.Steps.Single(step => step.Key == "repositories");
        repositories.Status.ShouldBe(Neo4jToMariaDbMigrationPlanStatuses.Ready);
        repositories.CanExecute.ShouldBeTrue();
        repositories.Counts!.Repositories.ShouldBe(1);

        var graph = report.Steps.Single(step => step.Key == "graph");
        graph.Status.ShouldBe(Neo4jToMariaDbMigrationPlanStatuses.Ready);
        graph.Counts!.Nodes.ShouldBe(1);
        graph.Counts.Edges.ShouldBe(1);

        report.BlockedAreas.ShouldBe(8);
    }

    [Fact]
    public async Task Service_RunRepositoriesAndGraphMigrationImportsNodesAndRemapsEdges()
    {
        var targetStore = new InMemoryGraphStore();
        var export = new Neo4jToMariaDbGraphExport(
            [new RepositoryEntity { Name = "Demo" }],
            [
                Node(100, "Demo", NodeLabel.Class, "Demo.Customer"),
                Node(101, "Demo", NodeLabel.Method, "Demo.Customer.Save")
            ],
            [
                Edge(200, "Demo", 100, 101, EdgeType.CALLS),
                Edge(201, "Demo", 100, 999, EdgeType.CALLS)
            ],
            [
                new CrossRepoEdge
                {
                    Id = 300,
                    SourceProject = "Demo",
                    TargetProject = "Demo",
                    SourceNodeId = 100,
                    TargetNodeId = 101,
                    Type = EdgeType.REFERENCES_PACKAGE
                }
            ]);
        var service = new Neo4jToMariaDbMigrationService(
            new Neo4jToMariaDbMigrationPlanner(),
            new FakeGraphExporter(export),
            targetStore);

        var result = await service.RunRepositoriesAndGraphMigrationAsync("codex");

        result.Status.ShouldBe("completed");
        result.Exported.Repositories.ShouldBe(1);
        result.Imported.Nodes.ShouldBe(2);
        result.Imported.Edges.ShouldBe(1);
        result.Imported.CrossRepoEdges.ShouldBe(1);
        result.SkippedEdges.ShouldBe(1);
        result.Checkpoints.ShouldContain(checkpoint => checkpoint.Area == "graph" && checkpoint.Stage == "import-edges");
        var repositories = await targetStore.ListRepositoriesAsync();
        repositories.Single().Name.ShouldBe("Demo");
        targetStore.Nodes.Count.ShouldBe(2);
        targetStore.Edges.Single().SourceId.ShouldNotBe(100);
        targetStore.CrossEdges.Single().SourceNodeId.ShouldBe(targetStore.Edges.Single().SourceId);
    }

    private static GraphNode Node(long id, string project, NodeLabel label, string qualifiedName) => new()
    {
        Id = id,
        Project = project,
        Label = label,
        Name = qualifiedName.Split('.').Last(),
        QualifiedName = qualifiedName
    };

    private static GraphEdge Edge(long id, string project, long sourceId, long targetId, EdgeType type) => new()
    {
        Id = id,
        Project = project,
        SourceId = sourceId,
        TargetId = targetId,
        Type = type
    };

    private sealed class FakeGraphExporter(Neo4jToMariaDbGraphExport export) : INeo4jToMariaDbGraphExporter
    {
        public Task<Neo4jToMariaDbGraphCounts> CountRepositoriesAndGraphAsync(CancellationToken ct = default)
            => Task.FromResult(export.Counts);

        public Task<Neo4jToMariaDbGraphExport> ExportRepositoriesAndGraphAsync(CancellationToken ct = default)
            => Task.FromResult(export);
    }
}
