using CodeGraph.Data.Migration;

namespace CodeGraph.Services.Migration;

public sealed class Neo4jToMariaDbMigrationPlanner
{
    public Neo4jToMariaDbMigrationPlanReport CreateDryRunReport(
        Neo4jToMariaDbMigrationManifest? manifest = null,
        DateTime? generatedAtUtc = null,
        Neo4jToMariaDbGraphCounts? repositoriesAndGraphCounts = null)
    {
        var sourceManifest = manifest ?? Neo4jToMariaDbMigrationManifest.Current;
        var steps = sourceManifest.Areas
            .OrderBy(area => area.Order)
            .Select((area, index) =>
            {
                var isImplementedArea = repositoriesAndGraphCounts is not null
                    && area.Key is "repositories" or "graph";
                var areaCounts = area.Key switch
                {
                    "repositories" when repositoriesAndGraphCounts is not null => new Neo4jToMariaDbMigrationPlanStepCounts(
                        Repositories: repositoriesAndGraphCounts.Repositories,
                        Nodes: 0,
                        Edges: 0,
                        CrossRepoEdges: 0),
                    "graph" when repositoriesAndGraphCounts is not null => new Neo4jToMariaDbMigrationPlanStepCounts(
                        Repositories: 0,
                        Nodes: repositoriesAndGraphCounts.Nodes,
                        Edges: repositoriesAndGraphCounts.Edges,
                        CrossRepoEdges: repositoriesAndGraphCounts.CrossRepoEdges),
                    _ => null
                };

                return new Neo4jToMariaDbMigrationPlanStep(
                Sequence: index + 1,
                Key: area.Key,
                Order: area.Order,
                Description: area.Description,
                ExporterKey: $"neo4j:{area.Key}",
                ImporterKey: $"mariadb:{area.Key}",
                CanExecute: isImplementedArea,
                Status: isImplementedArea
                    ? Neo4jToMariaDbMigrationPlanStatuses.Ready
                    : Neo4jToMariaDbMigrationPlanStatuses.Planned,
                Notes: isImplementedArea
                    ? "Dry run only. Neo4j exporter and MariaDB store import path are implemented for this area."
                    : "Dry run only. Source-specific Neo4j exporter and MariaDB importer are not implemented for this area yet.",
                BlockingReason: isImplementedArea
                    ? null
                    : "Exporter and importer implementation pending.",
                Counts: areaCounts);
            })
            .ToList();

        return new Neo4jToMariaDbMigrationPlanReport(
            DryRun: true,
            GeneratedAtUtc: generatedAtUtc ?? DateTime.UtcNow,
            Steps: steps);
    }
}

public sealed record Neo4jToMariaDbMigrationPlanReport(
    bool DryRun,
    DateTime GeneratedAtUtc,
    IReadOnlyList<Neo4jToMariaDbMigrationPlanStep> Steps)
{
    public int TotalAreas => Steps.Count;
    public bool CanExecute => Steps.All(step => step.CanExecute);
    public int BlockedAreas => Steps.Count(step => !step.CanExecute);
}

public sealed record Neo4jToMariaDbMigrationPlanStep(
    int Sequence,
    string Key,
    int Order,
    string Description,
    string ExporterKey,
    string ImporterKey,
    bool CanExecute,
    string Status,
    string Notes,
    string? BlockingReason,
    Neo4jToMariaDbMigrationPlanStepCounts? Counts = null);

public sealed record Neo4jToMariaDbMigrationPlanStepCounts(
    int Repositories,
    int Nodes,
    int Edges,
    int CrossRepoEdges);

public static class Neo4jToMariaDbMigrationPlanStatuses
{
    public const string Planned = "planned";
    public const string Ready = "ready";
}
