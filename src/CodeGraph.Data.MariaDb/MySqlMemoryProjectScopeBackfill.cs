using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// Migration 114's backfill for one project (CG-167), across every user's memory. A text names a
/// repository when the repository's name is one whole token of it, ignoring case: a token is a maximal
/// run of [a-z0-9._-] after lowercasing. Migration 114 replaced every other character by a space and
/// looked for " name " once per (row, repository); here one regular expression per row asks the same
/// question for all of the project's repositories at once: a name not preceded or followed by a token
/// character. Each statement only adds missing pairs (an existing scope keeps its origin), so a replay
/// changes nothing.
/// </summary>
public sealed partial class MySqlMemoryProjectScopeBackfill(IOptions<MariaDbStorageOptions> options) : IMemoryProjectScopeBackfill
{
    private const int MinimumNameLength = 3;
    private const int CommandTimeoutSeconds = 600;

    // A name made of other characters is never a whole token, so it can never match.
    [GeneratedRegex("^[a-z0-9._-]+$")]
    private static partial Regex TokenName();

    private static readonly string[] Statements =
    [
        // Entities whose label names a member repository.
        """
        INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
        SELECT entity.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
        FROM memory_entities_v2 entity
        WHERE LOWER(entity.label) REGEXP @pattern
        ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin
        """,
        // Claims with evidence whose source_ref names a member repository.
        """
        INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
        SELECT DISTINCT claim.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
        FROM memory_evidence evidence
        JOIN memory_claims claim ON claim.id = evidence.claim_id
        WHERE LOWER(evidence.source_ref) REGEXP @pattern
        ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin
        """,
        // Claims about an entity (subject or object) whose label names a member repository: matched on
        // the label itself, not on the scopes above, so a replay cannot widen the result transitively.
        """
        INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
        SELECT DISTINCT about.claim_id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
        FROM (
            SELECT claim.id AS claim_id
            FROM memory_entities_v2 entity JOIN memory_claims claim ON claim.subject_entity_id = entity.id
            WHERE LOWER(entity.label) REGEXP @pattern
            UNION
            SELECT claim.id
            FROM memory_entities_v2 entity JOIN memory_claims claim ON claim.object_entity_id = entity.id
            WHERE LOWER(entity.label) REGEXP @pattern
        ) about
        ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin
        """,
        // The entities the project's scoped claims are about (subject and object), as a write scopes them.
        """
        INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
        SELECT DISTINCT entity.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
        FROM (
            SELECT claim.subject_entity_id AS entity_id
            FROM memory_claim_projects scope JOIN memory_claims claim ON claim.id = scope.claim_id
            WHERE scope.project_key = @projectKey
            UNION
            SELECT claim.object_entity_id
            FROM memory_claim_projects scope JOIN memory_claims claim ON claim.id = scope.claim_id
            WHERE scope.project_key = @projectKey
        ) about
        JOIN memory_entities_v2 entity ON entity.id = about.entity_id
        ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin
        """,
    ];

    public async Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
    {
        var pattern = PatternOf(repositories);
        if (pattern is null || string.IsNullOrWhiteSpace(projectKey))
            return;

        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var statement in Statements)
            await connection.ExecuteAsync(new CommandDefinition(
                statement, new { projectKey, pattern }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>The expression matching any of the names as a whole token of a lowercased text, or null when no name can match.</summary>
    private static string? PatternOf(IEnumerable<string> repositories)
    {
        var names = repositories
            .Select(name => name.Trim().ToLowerInvariant())
            .Where(name => name.Length >= MinimumNameLength && TokenName().IsMatch(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0
            ? null
            : "(?<![a-z0-9._-])(" + string.Join("|", names.Select(name => name.Replace(".", "\\."))) + ")(?![a-z0-9._-])";
    }
}
