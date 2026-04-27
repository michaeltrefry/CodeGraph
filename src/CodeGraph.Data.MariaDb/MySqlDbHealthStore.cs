using CodeGraph.Data;
using CodeGraph.Models.Responses;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public class MySqlDbHealthStore(IOptions<MariaDbStorageOptions> optionsAccessor) : IDbHealthStore
{
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    private static readonly string[] ExpectedConstraints =
    [
        "repositories:PRIMARY",
        "nodes:PRIMARY",
        "nodes:uq_node",
        "edges:PRIMARY",
        "edges:uq_edge",
        "cross_repo_edges:PRIMARY",
        "cross_repo_edges:uq_xedge",
        "migration_history:PRIMARY",
        "migration_history:script_name",
        "repository_summaries:PRIMARY",
        "project_analyses:uq_project_analysis",
        "node_analysis:PRIMARY",
        "file_metrics:uq_file_metrics_project_path",
        "project_health_summaries:uq_project_health_project_dp",
        "project_security_summaries:uq_security_summary_project",
        "wiki_sections:uq_wiki_sections_slug",
        "wiki_pages:uq_wiki_pages_sibling_slug",
        "wiki_revisions:uq_wiki_revisions_page_rev",
        "job_schedules:uq_job_schedules_name"
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "nodes:idx_nodes_label",
        "nodes:idx_nodes_name",
        "nodes:idx_nodes_file",
        "analysis_batches:idx_ab_repo",
        "analysis_batches:idx_ab_status",
        "analysis_batch_requests:idx_abr_batch",
        "analysis_batch_requests:idx_abr_custom",
        "security_findings:ix_security_project",
        "security_findings:ix_security_severity",
        "wiki_pages:idx_wiki_pages_section",
        "wiki_pages:idx_wiki_pages_parent",
        "wiki_attachments:idx_wiki_attachments_page",
        "job_schedules:ix_job_schedules_due",
        "job_schedules:ix_job_schedules_lease_owner"
    ];

    public async Task<DatabaseHealthResponse> GetDatabaseHealthAsync()
    {
        await using var conn = new MySqlConnection(options.ConnectionString);
        await conn.OpenAsync();

        var capturedAt = DateTime.UtcNow;
        var constraints = (await conn.QueryAsync<string>("""
            SELECT CONCAT(table_name, ':', constraint_name)
            FROM information_schema.table_constraints
            WHERE table_schema = DATABASE()
              AND constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY')
            """)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var indexes = (await conn.QueryAsync<string>("""
            SELECT CONCAT(table_name, ':', index_name)
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND non_unique = 1
            GROUP BY table_name, index_name
            """)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingConstraints = ExpectedConstraints
            .Where(expected => !constraints.Contains(expected))
            .OrderBy(expected => expected, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingIndexes = ExpectedIndexes
            .Where(expected => !indexes.Contains(expected))
            .OrderBy(expected => expected, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateGroups = await GetDuplicateGroupsAsync(conn);
        var status = missingConstraints.Count > 0 || missingIndexes.Count > 0 || duplicateGroups.Count > 0
            ? "warning"
            : "healthy";

        return new DatabaseHealthResponse(
            status,
            capturedAt,
            constraints.Count,
            ExpectedConstraints.Length,
            missingConstraints,
            indexes.Count,
            ExpectedIndexes.Length,
            missingIndexes,
            OfflineIndexes: [],
            duplicateGroups);
    }

    private static async Task<List<DatabaseDuplicateGroupResponse>> GetDuplicateGroupsAsync(MySqlConnection conn)
    {
        var duplicates = await conn.QueryAsync<DuplicateGroupRow>("""
            SELECT 'CodeNode' AS Category,
                   CONCAT(project, '|', qualified_name) AS `Key`,
                   COUNT(*) AS `Count`,
                   JSON_ARRAYAGG(CAST(id AS CHAR)) AS SampleValuesJson
            FROM nodes
            GROUP BY project, qualified_name
            HAVING COUNT(*) > 1

            UNION ALL

            SELECT 'FileHash' AS Category,
                   CONCAT(project, '|', rel_path) AS `Key`,
                   COUNT(*) AS `Count`,
                   JSON_ARRAYAGG(content_hash) AS SampleValuesJson
            FROM file_hashes
            GROUP BY project, rel_path
            HAVING COUNT(*) > 1

            ORDER BY `Count` DESC, Category, `Key`
            LIMIT 50
            """);

        return duplicates.Select(row => new DatabaseDuplicateGroupResponse(
            row.Category,
            row.Key,
            row.Count,
            ParseSampleValues(row.SampleValuesJson))).ToList();
    }

    private static List<string> ParseSampleValues(string? sampleValuesJson)
    {
        if (string.IsNullOrWhiteSpace(sampleValuesJson))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(sampleValuesJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class DuplicateGroupRow
    {
        public string Category { get; init; } = "";
        public string Key { get; init; } = "";
        public int Count { get; init; }
        public string? SampleValuesJson { get; init; }
    }
}
