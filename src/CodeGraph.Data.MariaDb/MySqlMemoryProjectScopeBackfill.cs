using System.Text.RegularExpressions;
using CodeGraph.Models.Memory;
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
/// <para>
/// The range backfill (CG-172) asks the same questions of the memory written between two watermarks
/// only: a row id range of each table is a primary-key range, so its cost follows what was written,
/// not the size of memory. It relies on what ingestion never changes in place: an entity's label (a
/// merge keeps the existing one), an evidence row (each is new, with a fresh id) and a claim's subject
/// and object (its claim_key is derived from them).
/// </para>
/// </summary>
public sealed partial class MySqlMemoryProjectScopeBackfill(IOptions<MariaDbStorageOptions> options, TimeProvider? timeProvider = null)
    : IMemoryProjectScopeBackfill
{
    private const int MinimumNameLength = 3;
    // Bounded: a statement that times out fails its backfill, which stays pending for the next reconciliation.
    private const int CommandTimeoutSeconds = 120;

    /// <summary>
    /// How old a row must be before a watermark passes it. Ids are taken when a row is inserted, not when
    /// it commits, so a write still in flight can hold a lower id than a committed one; a watermark that
    /// passed it would never scan it. Only rows written this long ago count, and newer ones wait for the
    /// next run.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

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

    // The same four questions as Statements, of the rows above @*From and up to @*To only. Every row
    // read is reached through one of the ranges, by primary key or by an index from a row in a range.
    private static readonly RangeStatement[] RangeStatements =
    [
        // New entities whose label names a member repository.
        new(Ranges.Entities, """
            INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
            SELECT entity.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
            FROM memory_entities_v2 entity
            WHERE entity.id > @entityFrom AND entity.id <= @entityTo AND LOWER(entity.label) REGEXP @pattern
            ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin
            """),
        // Claims with new evidence whose source_ref names a member repository.
        new(Ranges.Evidence, """
            INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
            SELECT DISTINCT claim.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
            FROM memory_evidence evidence
            JOIN memory_claims claim ON claim.id = evidence.claim_id
            WHERE evidence.id > @evidenceFrom AND evidence.id <= @evidenceTo AND LOWER(evidence.source_ref) REGEXP @pattern
            ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin
            """),
        // Claims about an entity whose label names a member repository, where the claim or the entity is new.
        new(Ranges.Claims | Ranges.Entities, """
            INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
            SELECT DISTINCT about.claim_id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
            FROM (
                SELECT claim.id AS claim_id
                FROM memory_claims claim JOIN memory_entities_v2 entity ON entity.id = claim.subject_entity_id
                WHERE claim.id > @claimFrom AND claim.id <= @claimTo AND LOWER(entity.label) REGEXP @pattern
                UNION
                SELECT claim.id
                FROM memory_claims claim JOIN memory_entities_v2 entity ON entity.id = claim.object_entity_id
                WHERE claim.id > @claimFrom AND claim.id <= @claimTo AND LOWER(entity.label) REGEXP @pattern
                UNION
                SELECT claim.id
                FROM memory_entities_v2 entity
                JOIN memory_claims claim ON claim.username = entity.username AND claim.subject_entity_id = entity.id
                WHERE entity.id > @entityFrom AND entity.id <= @entityTo AND LOWER(entity.label) REGEXP @pattern
                UNION
                SELECT claim.id
                FROM memory_entities_v2 entity
                JOIN memory_claims claim ON claim.username = entity.username AND claim.object_entity_id = entity.id
                WHERE entity.id > @entityFrom AND entity.id <= @entityTo AND LOWER(entity.label) REGEXP @pattern
            ) about
            ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin
            """),
        // The entities of the project's claims that are new, have new evidence or are about a new entity:
        // every claim the statements above can have scoped, and every new claim a write scoped.
        new(Ranges.Claims | Ranges.Evidence | Ranges.Entities, """
            INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
            SELECT DISTINCT entity.id, @projectKey, 'backfill', UTC_TIMESTAMP(3)
            FROM (
                SELECT claim.id AS claim_id
                FROM memory_claims claim
                WHERE claim.id > @claimFrom AND claim.id <= @claimTo
                UNION
                SELECT evidence.claim_id
                FROM memory_evidence evidence
                WHERE evidence.id > @evidenceFrom AND evidence.id <= @evidenceTo AND evidence.claim_id IS NOT NULL
                UNION
                SELECT claim.id
                FROM memory_entities_v2 entity
                JOIN memory_claims claim ON claim.username = entity.username AND claim.subject_entity_id = entity.id
                WHERE entity.id > @entityFrom AND entity.id <= @entityTo
                UNION
                SELECT claim.id
                FROM memory_entities_v2 entity
                JOIN memory_claims claim ON claim.username = entity.username AND claim.object_entity_id = entity.id
                WHERE entity.id > @entityFrom AND entity.id <= @entityTo
            ) changed
            JOIN memory_claim_projects scope ON scope.claim_id = changed.claim_id AND scope.project_key = @projectKey
            JOIN memory_claims claim ON claim.id = changed.claim_id
            JOIN memory_entities_v2 entity ON entity.id = claim.subject_entity_id OR entity.id = claim.object_entity_id
            ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin
            """),
    ];

    public async Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
    {
        var pattern = PatternOf(repositories);
        if (pattern is null || string.IsNullOrWhiteSpace(projectKey))
            return;

        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var statement in Statements)
            await ExecuteAsync(connection, statement, new { projectKey, pattern }, ct);
    }

    public async Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        if (since is null)
        {
            // Primary-key maxima: no scan.
            var current = await connection.QuerySingleAsync<(long, long, long)>(new CommandDefinition(
                """
                SELECT (SELECT COALESCE(MAX(id), 0) FROM memory_entities_v2),
                       (SELECT COALESCE(MAX(id), 0) FROM memory_claims),
                       (SELECT COALESCE(MAX(id), 0) FROM memory_evidence)
                """, cancellationToken: ct));
            return new MemoryScopeWatermark(current.Item1, current.Item2, current.Item3);
        }

        // Reads the rows above the mark only (a primary-key range); a table keeps its mark when none of them has settled.
        var cutoff = clock.GetUtcNow().UtcDateTime - SettleTime;
        var settled = await connection.QuerySingleAsync<(long, long, long)>(new CommandDefinition(
            """
            SELECT COALESCE((SELECT MAX(id) FROM memory_entities_v2 WHERE id > @EntityId AND created_at <= @cutoff), @EntityId),
                   COALESCE((SELECT MAX(id) FROM memory_claims WHERE id > @ClaimId AND recorded_at <= @cutoff), @ClaimId),
                   COALESCE((SELECT MAX(id) FROM memory_evidence WHERE id > @EvidenceId AND created_at <= @cutoff), @EvidenceId)
            """,
            new { since.EntityId, since.ClaimId, since.EvidenceId, cutoff }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return new MemoryScopeWatermark(settled.Item1, settled.Item2, settled.Item3);
    }

    public async Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default)
    {
        var changed = (upTo.EntityId > after.EntityId ? Ranges.Entities : Ranges.None)
            | (upTo.ClaimId > after.ClaimId ? Ranges.Claims : Ranges.None)
            | (upTo.EvidenceId > after.EvidenceId ? Ranges.Evidence : Ranges.None);
        if (changed == Ranges.None)
            return;

        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var project in projects)
        {
            var pattern = PatternOf(project.Repositories);
            if (pattern is null || string.IsNullOrWhiteSpace(project.ProjectKey))
                continue;

            var parameters = new
            {
                projectKey = project.ProjectKey,
                pattern,
                entityFrom = after.EntityId,
                entityTo = upTo.EntityId,
                claimFrom = after.ClaimId,
                claimTo = upTo.ClaimId,
                evidenceFrom = after.EvidenceId,
                evidenceTo = upTo.EvidenceId,
            };
            // A statement whose ranges are all empty would find nothing, so it is not run.
            foreach (var statement in RangeStatements.Where(statement => (statement.Reads & changed) != Ranges.None))
                await ExecuteAsync(connection, statement.Sql, parameters, ct);
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string statement, object parameters, CancellationToken ct)
    {
        // READ COMMITTED reads the scanned memory rows consistently without locking them, so memory
        // ingestion is not blocked for the length of the scan (REPEATABLE READ would lock every row read).
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            statement, parameters, transaction, CommandTimeoutSeconds, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    [Flags]
    private enum Ranges
    {
        None = 0,
        Entities = 1,
        Claims = 2,
        Evidence = 4,
    }

    private sealed record RangeStatement(Ranges Reads, string Sql);

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
