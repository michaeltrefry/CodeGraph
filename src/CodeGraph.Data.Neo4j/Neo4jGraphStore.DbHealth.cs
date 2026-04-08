using Neo4j.Driver;
using CodeGraph.Models.Responses;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore : IDbHealthStore
{
    private static readonly string[] ExpectedConstraintNames =
    [
        "code_node_project_qn",
        "cross_repo_edge_unique",
        "repository_name",
        "repository_record_name",
        "sync_state_project",
        "file_hash_unique",
        "sequence_name",
        "migration_history_script",
        "repo_summary_project",
        "project_analysis_unique",
        "node_analysis_nodeid",
        "file_metrics_unique",
        "health_summary_unique",
        "health_analysis_unique",
        "security_summary_unique",
        "exclusion_rule_unique",
        "embedding_unique",
        "wiki_section_slug",
        "id_counter_label",
        "memory_entity_id",
        "memory_observation_id"
    ];

    private static readonly string[] ExpectedIndexNames =
    [
        "code_node_appid",
        "code_node_label",
        "code_node_name",
        "code_node_file",
        "code_node_dotnet_project",
        "cross_repo_edge_source",
        "cross_repo_edge_target",
        "analysis_batch_repo",
        "analysis_batch_status",
        "analysis_batch_request_batch",
        "analysis_batch_request_custom",
        "file_metrics_health",
        "security_finding_project",
        "security_finding_severity",
        "cluster_level",
        "cluster_id_level",
        "code_node_search",
        "embedding_vector",
        "wiki_section_appid",
        "wiki_page_appid",
        "wiki_page_section_slug",
        "wiki_page_section_parent",
        "wiki_revision_appid",
        "wiki_revision_page",
        "wiki_attachment_appid",
        "wiki_attachment_page",
        "settings_override_appid",
        "memory_entity_updatedAt",
        "memory_entity_fulltext",
        "memory_entity_embedding"
    ];

    public async Task<DatabaseHealthResponse> GetDatabaseHealthAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        var capturedAt = DateTime.UtcNow;

        var constraints = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                SHOW CONSTRAINTS
                YIELD name, ownedIndex
                RETURN name, ownedIndex
                """);

            var items = new List<(string Name, string? OwnedIndex)>();
            await foreach (var record in cursor)
            {
                items.Add((
                    record["name"].As<string>(),
                    record["ownedIndex"].As<object?>()?.ToString()));
            }

            return items;
        });

        var indexes = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                SHOW INDEXES
                YIELD name, type, state, entityType, labelsOrTypes, properties, failureMessage
                RETURN name, type, state, entityType, labelsOrTypes, properties, failureMessage
                """);

            var items = new List<DatabaseIndexIssueResponse>();
            await foreach (var record in cursor)
            {
                var type = record["type"].As<string>();
                if (string.Equals(type, "LOOKUP", StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new DatabaseIndexIssueResponse(
                    record["name"].As<string>(),
                    type,
                    record["state"].As<string>(),
                    record["entityType"].As<string>(),
                    ReadStringList(record["labelsOrTypes"].As<object?>()),
                    ReadStringList(record["properties"].As<object?>()),
                    record["failureMessage"].As<object?>()?.ToString()));
            }

            return items;
        });

        var duplicateGroups = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                CALL {
                    MATCH (s:Sequence)
                    WITH s.name AS key, count(*) AS itemCount, collect(toString(coalesce(s.value, 0)))[0..5] AS sampleValues
                    WHERE itemCount > 1
                    RETURN 'Sequence' AS category, key, itemCount, sampleValues

                    UNION ALL

                    MATCH (c:IdCounter)
                    WITH c.label AS key, count(*) AS itemCount, collect(toString(coalesce(c.current, 0)))[0..5] AS sampleValues
                    WHERE itemCount > 1
                    RETURN 'IdCounter' AS category, key, itemCount, sampleValues

                    UNION ALL

                    MATCH (n:CodeNode)
                    WITH coalesce(n.project, '') + '|' + coalesce(n.qualifiedName, '') AS key,
                         count(*) AS itemCount,
                         collect(toString(coalesce(n.appId, 0)))[0..5] AS sampleValues
                    WHERE itemCount > 1
                    RETURN 'CodeNode' AS category, key, itemCount, sampleValues
                }
                RETURN category, key, itemCount, sampleValues
                ORDER BY itemCount DESC, category, key
                LIMIT 50
                """);

            var items = new List<DatabaseDuplicateGroupResponse>();
            await foreach (var record in cursor)
            {
                items.Add(new DatabaseDuplicateGroupResponse(
                    record["category"].As<string>(),
                    record["key"].As<string>(),
                    record["itemCount"].As<int>(),
                    ReadStringList(record["sampleValues"].As<object?>())));
            }

            return items;
        });

        var actualConstraintNames = constraints
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ownedIndexNames = constraints
            .Select(c => c.OwnedIndex)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var namedIndexes = indexes
            .Where(index => !ownedIndexNames.Contains(index.Name))
            .ToList();

        var actualIndexNames = namedIndexes
            .Select(index => index.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingConstraints = ExpectedConstraintNames
            .Where(name => !actualConstraintNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingIndexes = ExpectedIndexNames
            .Where(name => !actualIndexNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var offlineIndexes = namedIndexes
            .Where(index => !string.Equals(index.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            .OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var status = offlineIndexes.Count > 0
            ? "critical"
            : missingConstraints.Count > 0 || missingIndexes.Count > 0 || duplicateGroups.Count > 0
                ? "warning"
                : "healthy";

        return new DatabaseHealthResponse(
            status,
            capturedAt,
            actualConstraintNames.Count,
            ExpectedConstraintNames.Length,
            missingConstraints,
            namedIndexes.Count,
            ExpectedIndexNames.Length,
            missingIndexes,
            offlineIndexes,
            duplicateGroups);
    }

    private static List<string> ReadStringList(object? raw)
    {
        return raw switch
        {
            null => [],
            IEnumerable<string> strings => strings.ToList(),
            IEnumerable<object?> objects => objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList(),
            _ => [raw.ToString() ?? string.Empty]
        };
    }
}
