using CodeGraph.Data.Migration;

namespace CodeGraph.Services.Migration;

public interface INeo4jToMariaDbMigrationService
{
    Task<Neo4jToMariaDbMigrationPlanReport> CreateDryRunReportAsync(
        DateTime? generatedAtUtc = null,
        CancellationToken ct = default);

    Task<Neo4jToMariaDbGraphImportResult> RunRepositoriesAndGraphMigrationAsync(
        string? requestedByUsername = null,
        CancellationToken ct = default);
}
