using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// The work tracker on MariaDB, ported from KanbanBoard's services (WorkItemWrites,
/// WorkItemDependencies, WorkItemClaims, WorkItemChangeLog, EpicPlans, IdempotencyKeys,
/// MariaDbNamedLock) onto Dapper. The concurrency discipline is KanbanBoard's:
/// <list type="bullet">
/// <item>Every write is one transaction whose first statement holds the row it depends on (an
/// UPDATE that changes nothing, or the conditional versioned UPDATE itself), so the reads after it
/// see what earlier holders committed and the version decides between concurrent updates.</item>
/// <item>Locks are taken in one order everywhere: a named lock (dependency adds, next_work, project
/// creates) before the transaction, then the project's row, then the feature's row, then rows of
/// work_items (several at once in key order), then the rows of work_resources (sc-27872; hold rows
/// are changed only under their resource's row), then knowledge rows (sc-27869: a write that stores
/// knowledge holds the project first, and inserts its entries after its item, resource and hold
/// writes), then the change-feed lock. Knowledge freshness baselines are adopted after the commit, on
/// another connection (KnowledgeBaselines), outside this order.</item>
/// <item>The change-feed entries are inserted and committed under the named lock
/// <see cref="ChangeLockName"/>, so a higher sequence always commits later and a reader's cursor
/// never skips an entry.</item>
/// <item>A transaction MariaDB rolls back as a deadlock victim is run again, three times in all,
/// and then refused <c>busy</c>.</item>
/// </list>
/// </summary>
public sealed partial class MySqlWorkStore(
    IOptions<MariaDbStorageOptions> optionsAccessor,
    TimeProvider? timeProvider = null,
    IOptions<WorkSessionOptions>? sessionOptions = null,
    Microsoft.Extensions.Logging.ILogger<MySqlWorkStore>? logger = null,
    IOptions<WorkLessonOptions>? lessonOptions = null) : IWorkStore
{
    // Distinct correction events after which a lesson with an applies-when activity becomes a rule (S9).
    private readonly int ruleThreshold = Math.Max(1, lessonOptions?.Value.RuleRecurrenceThreshold ?? new WorkLessonOptions().RuleRecurrenceThreshold);

    private readonly Microsoft.Extensions.Logging.ILogger log = logger ?? (Microsoft.Extensions.Logging.ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public const string ChangeLockName = "codegraph_work_changes";
    public const string DependencyLockName = "codegraph_work_dependencies";
    public const string ProjectCreateLockName = "codegraph_work_project_creates";
    public const int LockTimeoutSeconds = 10;
    public const int DeadlockAttempts = 3;

    // Idempotency scope of project creates, which have no parent.
    private static readonly Guid GlobalScope = Guid.Empty;

    private const int MySqlDeadlock = 1213;
    // ER_CHECKREAD: with innodb_snapshot_isolation (the default from MariaDB 11.6.2) a locking read of a
    // row changed after the transaction's snapshot fails instead of waiting. Retried like a deadlock.
    private const int MySqlRecordChanged = 1020;
    private const int MySqlLockWaitTimeout = 1205;
    private const int MySqlDuplicateEntry = 1062;

    private readonly MariaDbStorageOptions options = optionsAccessor.Value;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // A session silent this long is presumed dead; a session's claim lapses at its last heartbeat plus this.
    private readonly int idleSeconds = Math.Max(1, sessionOptions?.Value.IdleSeconds ?? new WorkSessionOptions().IdleSeconds);

    static MySqlWorkStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    // The precision MariaDB stores (DATETIME(6)).
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    public static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="body"/>, which begins and commits its own transaction on the connection
    /// it is given, while holding <paramref name="namedLocks"/> (taken in the order given, before any
    /// transaction, and released on the same connection). A deadlock victim is run again.
    /// </summary>
    private async Task<T> RunAsync<T>(IReadOnlyList<string> namedLocks, Func<MySqlConnection, Task<T>> body, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var held = new List<string>();
        try
        {
            foreach (var name in namedLocks)
            {
                // Inside the try, so RELEASE_LOCK follows even when the call is aborted after the
                // server granted the lock.
                held.Add(name);
                if (!await TryGetLockAsync(connection, name, ct))
                    throw WorkException.Busy($"Other writers held the lock '{name}' for {LockTimeoutSeconds} s. Nothing was changed; try again.");
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await body(connection);
                }
                catch (MySqlException exception) when (exception.Number is MySqlDeadlock or MySqlRecordChanged)
                {
                    // The server rolled the transaction back; nothing of the attempt is stored.
                    if (attempt == DeadlockAttempts)
                        throw WorkException.Busy($"Other writes deadlocked with this one {DeadlockAttempts} times. Nothing was changed; try again.");
                }
                catch (MySqlException exception) when (exception.Number == MySqlLockWaitTimeout)
                {
                    throw WorkException.Busy("Other writers held rows this write needs for too long. Nothing was changed; try again.");
                }
            }
        }
        finally
        {
            for (var index = held.Count - 1; index >= 0; index--)
                await ReleaseLockAsync(connection, held[index]);
        }
    }

    /// <summary>
    /// The server's name for a named lock of this store in <paramref name="database"/>. GET_LOCK
    /// names are server-wide, so the logical names are scoped to the database (as the migration
    /// lock is): two CodeGraph databases on one server never wait for each other. At most 64
    /// characters, MariaDB's limit.
    /// </summary>
    public static string ServerLockName(string database, string name) =>
        "codegraph:work:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{database.Trim().ToLowerInvariant()}\n{name}")))[..40];

    // Named locks are not transactional, but MySqlConnector wants every command of a connection
    // with an open transaction to name it.
    private static async Task<bool> TryGetLockAsync(MySqlConnection connection, string name, CancellationToken ct, MySqlTransaction? transaction = null) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT GET_LOCK(@name, @timeout)", new { name = ServerLockName(connection.Database, name), timeout = LockTimeoutSeconds },
            Active(transaction), cancellationToken: ct)) == 1;

    // Not cancellable: the lock must go even when the call was aborted. Releasing a lock this
    // connection does not hold returns 0 or NULL and changes nothing.
    private static async Task ReleaseLockAsync(MySqlConnection connection, string name, MySqlTransaction? transaction = null)
    {
        if (connection.State == ConnectionState.Open)
            await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT RELEASE_LOCK(@name)", new { name = ServerLockName(connection.Database, name) }, Active(transaction)));
    }

    // A transaction that has committed or rolled back no longer belongs to its connection.
    private static MySqlTransaction? Active(MySqlTransaction? transaction) => transaction?.Connection is null ? null : transaction;

    /// <summary>
    /// Appends the entries and commits, so the entries are stored exactly when the write is. Call it
    /// last. The INSERT and the COMMIT run under <see cref="ChangeLockName"/>, one writer at a time,
    /// so an entry with a higher sequence is always committed later. The lock is taken after every
    /// row lock of the write and its holder waits for nothing but its own INSERT and COMMIT, so it
    /// cannot be part of a deadlock.
    /// </summary>
    private async Task CommitAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<PendingChange> entries, WorkActor actor, DateTime now, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        var locked = false;
        try
        {
            locked = true;
            if (!await TryGetLockAsync(connection, ChangeLockName, ct, transaction))
                throw WorkException.Busy($"Other work changes were being recorded for {LockTimeoutSeconds} s. Nothing was changed; try again.");

            foreach (var chunk in entries.Chunk(100))
            {
                var sql = new StringBuilder(
                    "INSERT INTO work_changes (project_id, project_key, entity_kind, entity_id, entity_key, kind, actor_name, actor_kind, actor_session_id, changes, created_at) VALUES ");
                var parameters = new DynamicParameters();
                for (var index = 0; index < chunk.Length; index++)
                {
                    var entry = chunk[index];
                    if (index > 0)
                        sql.Append(',');
                    sql.Append($"(@p{index},@k{index},@e{index},@i{index},@ek{index},@kind{index},@an,@ak,@asession,@c{index},@now)");
                    parameters.Add($"p{index}", entry.ProjectId);
                    parameters.Add($"k{index}", entry.ProjectKey);
                    parameters.Add($"e{index}", JsonName(entry.Entity));
                    parameters.Add($"i{index}", entry.EntityId);
                    parameters.Add($"ek{index}", entry.EntityKey);
                    parameters.Add($"kind{index}", entry.Kind);
                    parameters.Add($"c{index}", entry.Changes.ToJsonString(WorkJson.Options));
                }

                parameters.Add("an", actor.Name);
                parameters.Add("ak", actor.Kind);
                parameters.Add("asession", actor.SessionId);
                parameters.Add("now", now);
                await connection.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (locked)
                await ReleaseLockAsync(connection, ChangeLockName, transaction);
        }
    }

    private static string JsonName<TEnum>(TEnum value) where TEnum : struct, Enum => WorkJson.Name(value);

    private static TEnum ParseName<TEnum>(string stored) where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => WorkJson.Name(value) == stored);

    // ---- Change entries ----------------------------------------------------------------------

    private sealed record PendingChange(
        Guid ProjectId,
        string ProjectKey,
        WorkEntityKind Entity,
        Guid EntityId,
        string EntityKey,
        string Kind,
        JsonArray Changes);

    /// <summary>
    /// One <c>{field, old, new}</c> per field whose value differs between the two snapshots (a
    /// missing side is null). Values are compared as JSON, so lists compare by content.
    /// </summary>
    private static JsonArray Diff(IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after)
    {
        var changes = new JsonArray();
        foreach (var field in (before?.Keys ?? []).Union(after?.Keys ?? []))
        {
            var oldValue = Node(before?.GetValueOrDefault(field));
            var newValue = Node(after?.GetValueOrDefault(field));
            if (!JsonNode.DeepEquals(oldValue, newValue))
                changes.Add(Change(field, oldValue, newValue));
        }

        return changes;
    }

    private static JsonObject Change(string field, JsonNode? oldValue, JsonNode? newValue)
    {
        var change = new JsonObject { ["field"] = field };
        change["old"] = Cut(oldValue, out var oldTruncated);
        change["new"] = Cut(newValue, out var newTruncated);
        // Present only when true: read the object for the whole text.
        if (oldTruncated)
            change["old_truncated"] = true;
        if (newTruncated)
            change["new_truncated"] = true;
        return change;
    }

    private static JsonObject Change(string field, object? oldValue, object? newValue) =>
        Change(field, Node(oldValue), Node(newValue));

    private static JsonNode? Node(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, WorkJson.Options);

    // Every text of a value is cut to MaxStoredChangeText characters: a 9 MB description would
    // otherwise put 18 MB in one entry, more than the default max_allowed_packet.
    private static JsonNode? Cut(JsonNode? node, out bool truncated)
    {
        truncated = false;
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > WorkLimits.MaxStoredChangeText:
                truncated = true;
                return JsonValue.Create(CutText(text));
            case JsonObject fields:
                foreach (var (name, inner) in fields.ToList())
                {
                    if (inner is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText) && innerText.Length > WorkLimits.MaxStoredChangeText)
                    {
                        fields[name] = CutText(innerText);
                        truncated = true;
                    }
                }

                return fields;
            default:
                return node;
        }
    }

    private static string CutText(string text)
    {
        var length = WorkLimits.MaxStoredChangeText;
        // Not between the two halves of one character.
        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }

    // ---- Rows --------------------------------------------------------------------------------

    private sealed class ProjectRow
    {
        public Guid Id { get; init; }
        public string ProjectKey { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string GoalsJson { get; init; } = "[]";
        public int NextItemNumber { get; init; }
        public int NextFeatureNumber { get; init; }
        public long Version { get; init; }
        public string CreatedBy { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class FeatureRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public long Version { get; init; }
        public string CreatedBy { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? GoalsJson { get; init; }
        public string? RequirementsJson { get; init; }
        public string? AcceptanceTestsJson { get; init; }
        public string? ImportedJson { get; init; }
        public bool RequirementsUnreconciled { get; init; }

        public string Key => WorkKeys.Feature(ProjectKey, Number);
    }

    private sealed class ItemRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public Guid? FeatureId { get; init; }
        public string ItemType { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public string Status { get; init; } = "";
        public string Priority { get; init; } = "";
        public int SortOrder { get; init; }
        public string? Labels { get; init; }
        public string? RepositoryName { get; init; }
        public string? ClaimHolder { get; init; }
        public string? ClaimMarker { get; init; }
        public string? ClaimSessionId { get; init; }
        public DateTime? ClaimedAt { get; init; }
        public DateTime? ClaimExpiresAt { get; init; }
        public long Version { get; init; }
        public string CreatedBy { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? PlanRef { get; init; }
        public string? Goal { get; init; }
        public string? AcceptanceJson { get; init; }
        public string? InheritsJson { get; init; }
        public string? TouchesJson { get; init; }
        public string? AcceptanceReportJson { get; init; }
        public bool MergedAwaitingAcceptance { get; init; }
        public string? ImportedJson { get; init; }

        public string Key => WorkKeys.Item(ProjectKey, Number);

        public bool HasLiveClaim(DateTime now) => ClaimHolder is not null && ClaimExpiresAt > now;
    }

    private const string ItemColumns =
        "i.id, i.project_id, p.project_key, i.number, i.feature_id, i.item_type, i.title, i.description, i.status, i.priority, " +
        "i.sort_order, i.labels, i.repository_name, i.claim_holder, i.claim_marker, i.claim_session_id, i.claimed_at, " +
        "i.claim_expires_at, i.version, i.created_by, i.created_at, i.updated_at, " +
        "i.plan_ref, i.goal, i.acceptance_json, i.inherits_json, i.touches_json, i.acceptance_report_json, i.merged_awaiting_acceptance, i.imported_json";

    private const string FeatureColumns =
        "f.id, f.project_id, p.project_key, f.number, f.title, f.description, f.version, f.created_by, f.created_at, f.updated_at, " +
        "f.goals_json, f.requirements_json, f.acceptance_tests_json, f.imported_json, f.requirements_unreconciled";

    private const string ProjectColumns =
        "p.id, p.project_key, p.name, p.description, p.goals_json, p.next_item_number, p.next_feature_number, p.version, p.created_by, p.created_at, p.updated_at";

    // The condition, on alias i, for an item next_work may hand out at @now.
    private const string AvailableCondition =
        "i.status = 'ready' AND (i.claim_holder IS NULL OR i.claim_expires_at IS NULL OR i.claim_expires_at <= @now) " +
        "AND NOT EXISTS (SELECT 1 FROM work_item_dependencies d JOIN work_items b ON b.id = d.blocker_item_id " +
        "WHERE d.blocked_item_id = i.id AND b.status <> 'complete') AND NOT " + OpenBlockingQuestion;

    // The condition, on alias i, for an item next_work recovers at @now (§5.9): in progress, every
    // blocker complete, and no live claim (its claim lapsed with its session, or was released).
    // It must have been claimed: a claim that lapsed in place (holder still on the row) or a session
    // claim in its history. An item a human moved to in progress is not "whose claim lapsed".
    private const string RecoverableCondition =
        "i.status = 'in_progress' AND (i.claim_holder IS NULL OR i.claim_expires_at IS NULL OR i.claim_expires_at <= @now) " +
        "AND (i.claim_holder IS NOT NULL OR EXISTS (SELECT 1 FROM work_session_claims sc WHERE sc.item_id = i.id) " +
        "OR EXISTS (SELECT 1 FROM work_attempts att WHERE att.item_id = i.id)) " +
        "AND NOT EXISTS (SELECT 1 FROM work_item_dependencies d JOIN work_items b ON b.id = d.blocker_item_id " +
        "WHERE d.blocked_item_id = i.id AND b.status <> 'complete') AND NOT " + OpenBlockingQuestion;

    // Blocked by an unfinished blocker, or by an open question to the human that blocks it (sc-27871).
    private const string BlockedCondition =
        "(EXISTS (SELECT 1 FROM work_item_dependencies d JOIN work_items b ON b.id = d.blocker_item_id " +
        "WHERE d.blocked_item_id = i.id AND b.status <> 'complete') OR " + OpenBlockingQuestion + ")";

    private static Task<ItemRow?> ReadItemRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE i.id = @itemId",
            new { itemId }, transaction, cancellationToken: ct));

    private static Task<ProjectRow?> ReadProjectRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid projectId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            $"SELECT {ProjectColumns} FROM work_projects p WHERE p.id = @projectId",
            new { projectId }, transaction, cancellationToken: ct));

    private static Task<FeatureRow?> ReadFeatureRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid featureId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<FeatureRow>(new CommandDefinition(
            $"SELECT {FeatureColumns} FROM work_features f JOIN work_projects p ON p.id = f.project_id WHERE f.id = @featureId",
            new { featureId }, transaction, cancellationToken: ct));

    // ---- Views -------------------------------------------------------------------------------

    private async Task<ProjectView?> LoadProjectViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid projectId, bool withCounts, CancellationToken ct)
    {
        var row = await ReadProjectRowAsync(connection, transaction, projectId, ct);
        if (row is null)
            return null;

        var repositories = await ReadRepositoriesAsync(connection, transaction, projectId, ct);
        var counts = withCounts
            ? await CountsAsync(connection, transaction, "project_id = @id", projectId, ct)
            : null;
        return ToView(row, repositories, counts);
    }

    private static ProjectView ToView(ProjectRow row, IReadOnlyList<RepositoryMemberView> repositories, IReadOnlyDictionary<string, int>? counts) =>
        new(row.Id, row.ProjectKey, row.Name, row.Description,
            JsonSerializer.Deserialize<List<string>>(row.GoalsJson) ?? [],
            repositories, row.Version, row.CreatedBy, Utc(row.CreatedAt), Utc(row.UpdatedAt), counts);

    private static async Task<IReadOnlyList<RepositoryMemberView>> ReadRepositoriesAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid projectId, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(string RepositoryName, string Role)>(new CommandDefinition(
            "SELECT repository_name, role FROM work_project_repositories WHERE project_id = @projectId ORDER BY position, repository_name",
            new { projectId }, transaction, cancellationToken: ct));
        return rows.Select(row => new RepositoryMemberView(row.RepositoryName, ParseName<RepositoryRole>(row.Role))).ToList();
    }

    private static async Task<IReadOnlyDictionary<string, int>> CountsAsync(MySqlConnection connection, MySqlTransaction? transaction, string where, Guid id, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(string Status, int Count)>(new CommandDefinition(
            $"SELECT status, COUNT(*) FROM work_items WHERE {where} GROUP BY status", new { id }, transaction, cancellationToken: ct));
        return rows.ToDictionary(row => row.Status, row => row.Count);
    }

    private async Task<FeatureView?> LoadFeatureViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid featureId, bool withCounts, CancellationToken ct)
    {
        var row = await ReadFeatureRowAsync(connection, transaction, featureId, ct);
        if (row is null)
            return null;

        var documents = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            "SELECT id, title, body, position, created_at, updated_at FROM work_feature_documents WHERE feature_id = @featureId ORDER BY position",
            new { featureId }, transaction, cancellationToken: ct));
        var counts = withCounts
            ? await CountsAsync(connection, transaction, "feature_id = @id", featureId, ct)
            : null;
        var (status, completion, requirements) = await CompletionAsync(connection, transaction, row, ct);
        var goals = ReadList<string>(row.GoalsJson);
        var tests = ReadList<string>(row.AcceptanceTestsJson);
        return new FeatureView(
            row.Id, WorkKeys.Feature(row.ProjectKey, row.Number), row.ProjectId, row.ProjectKey, row.Title, row.Description,
            documents.Select(document => new DocumentView(document.Id, document.Title, document.Body, document.Position, Utc(document.CreatedAt), Utc(document.UpdatedAt))).ToList(),
            row.Version, row.CreatedBy, Utc(row.CreatedAt), Utc(row.UpdatedAt), counts,
            status, completion, goals.Count == 0 ? null : goals, requirements.Count == 0 ? null : requirements, tests.Count == 0 ? null : tests,
            ReadImported(row.ImportedJson),
            // Reconciled once a planning agent has given the feature structured requirements.
            row.RequirementsUnreconciled && requirements.Count == 0 ? true : null);
    }

    private static ImportedView? ReadImported(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ImportedView>(json, WorkJson.Options);

    private sealed class DocumentRow
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public int Position { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class DependencyRow
    {
        public Guid Id { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private sealed class LinkRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public string Url { get; init; } = "";
        public DateTime CreatedAt { get; init; }
    }

    private sealed class CommentRow
    {
        public Guid Id { get; init; }
        public string Author { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public string? AuthorSessionId { get; init; }
        public string? Harness { get; init; }
        public string AuthorKind { get; init; } = "";
        public DateTime? SourceCreatedAt { get; init; }
    }

    private sealed class AttachmentRow
    {
        public Guid Id { get; init; }
        public string Filename { get; init; } = "";
        public string ContentType { get; init; } = "";
        public long SizeBytes { get; init; }
        public string? StoragePath { get; init; }
        public string? SkippedReason { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private async Task<ItemView?> LoadItemViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct, bool fullHandoffs = false)
    {
        var row = await ReadItemRowAsync(connection, transaction, itemId, ct);
        return row is null ? null : await ToViewAsync(connection, transaction, row, ct, fullHandoffs);
    }

    private async Task<ItemView> ToViewAsync(MySqlConnection connection, MySqlTransaction? transaction, ItemRow row, CancellationToken ct, bool fullHandoffs = false)
    {
        var now = Now();
        var blockedBy = (await connection.QueryAsync<DependencyRow>(new CommandDefinition(
            "SELECT b.id, p.project_key, b.number, b.title, b.status FROM work_item_dependencies d " +
            "JOIN work_items b ON b.id = d.blocker_item_id JOIN work_projects p ON p.id = b.project_id " +
            "WHERE d.blocked_item_id = @id ORDER BY b.number", new { id = row.Id }, transaction, cancellationToken: ct))).Select(ToView).ToList();
        var blocks = (await connection.QueryAsync<DependencyRow>(new CommandDefinition(
            "SELECT b.id, p.project_key, b.number, b.title, b.status FROM work_item_dependencies d " +
            "JOIN work_items b ON b.id = d.blocked_item_id JOIN work_projects p ON p.id = b.project_id " +
            "WHERE d.blocker_item_id = @id ORDER BY b.number", new { id = row.Id }, transaction, cancellationToken: ct))).Select(ToView).ToList();
        var links = (await connection.QueryAsync<LinkRow>(new CommandDefinition(
            "SELECT id, kind, url, created_at FROM work_item_links WHERE item_id = @id ORDER BY created_at, id",
            new { id = row.Id }, transaction, cancellationToken: ct)))
            .Select(link => new LinkView(link.Id, ParseName<WorkLinkKind>(link.Kind), link.Url, Utc(link.CreatedAt))).ToList();
        var comments = (await connection.QueryAsync<CommentRow>(new CommandDefinition(
            "SELECT c.id, c.author, c.author_kind, c.body, c.created_at, c.source_created_at, c.author_session_id, COALESCE(s.harness, c.author_harness) AS harness FROM work_item_comments c " +
            "LEFT JOIN work_sessions s ON s.id = c.author_session_id WHERE c.item_id = @id ORDER BY c.created_at, c.id",
            new { id = row.Id }, transaction, cancellationToken: ct)))
            .Select(comment => new CommentView(comment.Id, comment.Author, comment.Body, Utc(comment.CreatedAt), comment.AuthorSessionId, comment.Harness,
                comment.AuthorKind is WorkActor.AgentKind or WorkActor.SystemKind ? comment.AuthorKind : null,
                comment.SourceCreatedAt is { } source && source != comment.CreatedAt ? Utc(source) : null)).ToList();
        var attachments = row.ImportedJson is null
            ? []
            : (await connection.QueryAsync<AttachmentRow>(new CommandDefinition(
                "SELECT id, filename, content_type, size_bytes, storage_path, skipped_reason, created_at FROM work_item_attachments WHERE item_id = @id ORDER BY created_at, id",
                new { id = row.Id }, transaction, cancellationToken: ct)))
            .Select(file => new AttachmentView(file.Id, file.Filename, file.ContentType, file.SizeBytes, file.StoragePath is not null, Utc(file.CreatedAt), file.SkippedReason)).ToList();
        // E4: the latest handoff and a count; the whole history only when asked for.
        var handoffs = await ReadHandoffsAsync(connection, transaction, row.Id, limit: fullHandoffs ? null : 1, ct);
        var handoffCount = fullHandoffs
            ? handoffs.Count
            : (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM work_handoffs WHERE item_id = @id", new { id = row.Id }, transaction, cancellationToken: ct));

        EntityRef? feature = null;
        IReadOnlyList<RequirementView> requirements = [];
        if (row.FeatureId is { } featureId)
        {
            var featureRow = await connection.QuerySingleAsync<(int Number, string Title, string? RequirementsJson)>(new CommandDefinition(
                "SELECT number, title, requirements_json FROM work_features WHERE id = @featureId", new { featureId }, transaction, cancellationToken: ct));
            feature = new EntityRef(featureId, WorkKeys.Feature(row.ProjectKey, featureRow.Number), featureRow.Title);
            requirements = ReadList<RequirementView>(featureRow.RequirementsJson);
        }

        // The brief (sc-27868): criteria, the requirements the item inherits as its feature states
        // them, what it touches, and its execution attempts (the latest three, all with detail full).
        var criteria = ReadList<AcceptanceCriterionView>(row.AcceptanceJson);
        var inherits = ReadList<string>(row.InheritsJson);
        var inherited = inherits.Select(id => requirements.FirstOrDefault(requirement => requirement.Id == id)).OfType<RequirementView>().ToList();
        var touches = ReadList<string>(row.TouchesJson);
        var attempts = await ReadAttemptsAsync(connection, transaction, row.Id, fullHandoffs ? null : 3, ct);
        var attemptCount = fullHandoffs
            ? attempts.Count
            : (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM work_attempts WHERE item_id = @id", new { id = row.Id }, transaction, cancellationToken: ct));
        var report = row.AcceptanceReportJson is { } reportJson ? JsonSerializer.Deserialize<AcceptanceReportView>(reportJson, WorkJson.Options) : null;

        var unfinished = blockedBy.Where(blocker => blocker.Status != WorkStatus.Complete).ToList();
        // An open blocking question to the human blocks the item too; a dependency's reason comes first.
        var awaitingHuman = await HasOpenBlockingQuestionAsync(connection, transaction, row.Id, ct);
        string? reason = unfinished.Count > 0
            ? unfinished.Any(blocker => blocker.Status == WorkStatus.Closed) ? "cancelled_prerequisite" : "waiting"
            : awaitingHuman ? "awaiting_human" : null;

        return new ItemView(
            row.Id, row.Key, row.ProjectId, row.ProjectKey, feature,
            ParseName<WorkItemType>(row.ItemType), row.Title, row.Description,
            WorkStatuses.Parse(row.Status), ParseName<WorkPriority>(row.Priority), row.SortOrder,
            SplitLabels(row.Labels), row.RepositoryName,
            unfinished.Count > 0 || awaitingHuman, reason, blockedBy, blocks,
            row.HasLiveClaim(now)
                ? new ClaimView(row.ClaimHolder!, row.ClaimMarker!, Utc(row.ClaimedAt!.Value), Utc(row.ClaimExpiresAt!.Value), row.ClaimSessionId)
                : null,
            links, comments, row.Version, row.CreatedBy, Utc(row.CreatedAt), Utc(row.UpdatedAt),
            handoffs.Count == 0 ? null : handoffs, handoffCount == 0 ? null : handoffCount,
            row.PlanRef, row.Goal,
            criteria.Count == 0 ? null : criteria,
            inherits.Count == 0 ? null : inherits,
            inherited.Count == 0 ? null : inherited,
            touches.Count == 0 ? null : touches,
            row.MergedAwaitingAcceptance ? true : null,
            report,
            attempts.Count == 0 ? null : attempts,
            attemptCount == 0 ? null : attemptCount,
            Imported: ReadImported(row.ImportedJson),
            Attachments: attachments.Count == 0 ? null : attachments);
    }

    private static DependencyView ToView(DependencyRow row) =>
        new(row.Id, WorkKeys.Item(row.ProjectKey, row.Number), row.Title, WorkStatuses.Parse(row.Status));

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static IReadOnlyList<string> SplitLabels(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string? JoinLabels(IReadOnlyList<string> labels) =>
        labels.Count == 0 ? null : string.Join(',', labels);

    // ---- Reads -------------------------------------------------------------------------------

    private sealed class TargetRow
    {
        public string Kind { get; init; } = "";
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
    }

    private static async Task<TargetRow?> FindAliasAsync(MySqlConnection connection, string value, CancellationToken ct) =>
        WorkReferences.NormalizeAlias(value) is { } alias
            ? await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(
                "SELECT 'item' AS kind, i.id, i.project_id FROM work_item_aliases a JOIN work_items i ON i.id = a.item_id WHERE a.alias = @alias",
                new { alias }, cancellationToken: ct))
            : null;

    public async Task<WorkTarget?> ResolveAsync(string idOrKey, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var value = idOrKey.Trim();
        if (Guid.TryParse(value, out var id))
        {
            var found = await connection.QueryFirstOrDefaultAsync<TargetRow>(new CommandDefinition(
                "SELECT 'item' AS kind, id, project_id FROM work_items WHERE id = @id " +
                "UNION ALL SELECT 'feature', id, project_id FROM work_features WHERE id = @id " +
                "UNION ALL SELECT 'project', id, id FROM work_projects WHERE id = @id",
                new { id }, cancellationToken: ct));
            return found is null ? null : new WorkTarget(ParseName<WorkEntityKind>(found.Kind), found.Id, found.ProjectId);
        }

        if (WorkKeys.TryParse(value, out var projectKey, out var kind, out var number))
        {
            var sql = kind == WorkEntityKind.Item
                ? "SELECT 'item' AS kind, i.id, i.project_id FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE p.project_key = @projectKey AND i.number = @number"
                : "SELECT 'feature' AS kind, f.id, f.project_id FROM work_features f JOIN work_projects p ON p.id = f.project_id WHERE p.project_key = @projectKey AND f.number = @number";
            var hit = await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(sql, new { projectKey, number }, cancellationToken: ct));
            // An external alias (an imported story's sc-27866) resolves like a key (sc-27870).
            if (hit is null && kind == WorkEntityKind.Item)
                hit = await FindAliasAsync(connection, value, ct);
            return hit is null ? null : new WorkTarget(kind, hit.Id, hit.ProjectId);
        }

        // sc-0012 (leading zeros) is not a key but names the alias sc-12.
        if (await FindAliasAsync(connection, value, ct) is { } aliased)
            return new WorkTarget(WorkEntityKind.Item, aliased.Id, aliased.ProjectId);

        var key = value.ToUpperInvariant();
        if (!WorkKeys.IsValidProjectKey(key))
            return null;

        var projectId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM work_projects WHERE project_key = @key", new { key }, cancellationToken: ct));
        return projectId is { } pid ? new WorkTarget(WorkEntityKind.Project, pid, pid) : null;
    }

    public async Task<ProjectView?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await LoadProjectViewAsync(connection, null, projectId, withCounts: true, ct);
    }

    public async Task<FeatureView?> GetFeatureAsync(Guid featureId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // One snapshot for the feature, its counts and its derived completion, so the status never
        // mixes item states from before and after a concurrent write.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var view = await LoadFeatureViewAsync(connection, transaction, featureId, withCounts: true, ct);
        await transaction.CommitAsync(ct);
        return view;
    }

    public async Task<ItemView?> GetItemAsync(Guid itemId, bool fullHandoffs, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // One snapshot for the whole brief.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var view = await LoadItemViewAsync(connection, transaction, itemId, ct, fullHandoffs);
        // The brief carries the thread's open questions and requests (§5.9), whoever reads it.
        if (view is not null)
            view = view with { OpenMessages = await ReadOpenMessagesAsync(connection, transaction, itemId, ct) };
        await transaction.CommitAsync(ct);
        return view;
    }

    public async Task<FindWorkResult> FindAsync(FindWorkSpec spec, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return spec.Kind switch
        {
            WorkEntityKind.Project => await FindProjectsAsync(connection, spec, ct),
            WorkEntityKind.Feature => await FindFeaturesAsync(connection, spec, ct),
            _ => await FindItemsAsync(connection, spec, ct)
        };
    }

    private async Task<FindWorkResult> FindProjectsAsync(MySqlConnection connection, FindWorkSpec spec, CancellationToken ct)
    {
        var parameters = new DynamicParameters(new { limit = spec.Limit, after = spec.After ?? string.Empty });
        var where = new List<string> { "p.project_key > @after" };
        AddWords(where, parameters, spec.Words, "p.name", "p.description");
        var rows = (await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            $"SELECT {ProjectColumns} FROM work_projects p WHERE {string.Join(" AND ", where)} ORDER BY p.project_key LIMIT @limit",
            parameters, cancellationToken: ct))).ToList();

        var results = new List<object>();
        foreach (var row in rows)
        {
            results.Add(spec.Detail == WorkDetail.Full
                ? (await LoadProjectViewAsync(connection, null, row.Id, withCounts: true, ct))!
                : new WorkRow(row.Id, row.ProjectKey, "project", row.Name, Version: row.Version));
        }

        return new FindWorkResult(results, rows.Count == spec.Limit ? rows[^1].ProjectKey : null);
    }

    private async Task<FindWorkResult> FindFeaturesAsync(MySqlConnection connection, FindWorkSpec spec, CancellationToken ct)
    {
        var parameters = new DynamicParameters(new { projectId = spec.ProjectId, limit = spec.Limit, after = ParseAfter(spec.After) });
        var where = new List<string> { "f.project_id = @projectId", "f.number > @after" };
        AddWords(where, parameters, spec.Words, "f.title", "f.description");
        var rows = (await connection.QueryAsync<FeatureRow>(new CommandDefinition(
            $"SELECT {FeatureColumns} FROM work_features f JOIN work_projects p ON p.id = f.project_id WHERE {string.Join(" AND ", where)} ORDER BY f.number LIMIT @limit",
            parameters, cancellationToken: ct))).ToList();

        var results = new List<object>();
        foreach (var row in rows)
        {
            results.Add(spec.Detail == WorkDetail.Full
                ? (await LoadFeatureViewAsync(connection, null, row.Id, withCounts: true, ct))!
                : new WorkRow(row.Id, WorkKeys.Feature(row.ProjectKey, row.Number), "feature", row.Title, Version: row.Version));
        }

        return new FindWorkResult(results, rows.Count == spec.Limit ? rows[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private sealed class ItemListRow
    {
        public Guid Id { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public string ItemType { get; init; } = "";
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public string Priority { get; init; } = "";
        public string? ClaimHolder { get; init; }
        public DateTime? ClaimExpiresAt { get; init; }
        public long Version { get; init; }
        public bool Blocked { get; init; }
    }

    private async Task<FindWorkResult> FindItemsAsync(MySqlConnection connection, FindWorkSpec spec, CancellationToken ct)
    {
        var now = Now();
        var parameters = new DynamicParameters(new { projectId = spec.ProjectId, limit = spec.Limit, after = ParseAfter(spec.After), now });
        var where = new List<string> { "i.project_id = @projectId", "i.number > @after" };
        if (spec.Statuses is { Count: > 0 } statuses)
        {
            where.Add("i.status IN @statuses");
            parameters.Add("statuses", statuses.Select(WorkStatuses.Name).ToArray());
        }
        else
        {
            where.Add("i.status <> 'closed'");
        }

        if (spec.Type is { } type)
        {
            where.Add("i.item_type = @type");
            parameters.Add("type", JsonName(type));
        }

        if (spec.Priority is { } priority)
        {
            where.Add("i.priority = @priority");
            parameters.Add("priority", JsonName(priority));
        }

        if (spec.Repository is { } repository)
        {
            where.Add("i.repository_name = @repository");
            parameters.Add("repository", repository);
        }

        if (spec.FeatureId is { } featureId)
        {
            where.Add("i.feature_id = @featureId");
            parameters.Add("featureId", featureId);
        }

        if (spec.Label is { } label)
        {
            where.Add(LabelCondition);
            parameters.Add("label", label);
        }

        if (spec.Ready is { } ready)
            where.Add(ready ? $"({AvailableCondition})" : $"NOT ({AvailableCondition})");

        if (spec.Claimed is { } claimed)
            where.Add(claimed ? "(i.claim_holder IS NOT NULL AND i.claim_expires_at > @now)" : "(i.claim_holder IS NULL OR i.claim_expires_at <= @now)");

        AddWords(where, parameters, spec.Words, "i.title", "i.description");

        var rows = (await connection.QueryAsync<ItemListRow>(new CommandDefinition(
            $"SELECT i.id, p.project_key, i.number, i.item_type, i.title, i.status, i.priority, i.claim_holder, i.claim_expires_at, i.version, " +
            $"{BlockedCondition} AS blocked FROM work_items i JOIN work_projects p ON p.id = i.project_id " +
            $"WHERE {string.Join(" AND ", where)} ORDER BY i.number LIMIT @limit",
            parameters, cancellationToken: ct))).ToList();

        var results = new List<object>();
        foreach (var row in rows)
        {
            results.Add(spec.Detail == WorkDetail.Full
                ? (await LoadItemViewAsync(connection, null, row.Id, ct))!
                : new WorkRow(
                    row.Id, WorkKeys.Item(row.ProjectKey, row.Number), "item", row.Title,
                    WorkStatuses.Parse(row.Status), ParseName<WorkPriority>(row.Priority), ParseName<WorkItemType>(row.ItemType),
                    row.Blocked, row.ClaimHolder is not null && row.ClaimExpiresAt > now ? row.ClaimHolder : null, row.Version));
        }

        return new FindWorkResult(results, rows.Count == spec.Limit ? rows[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    // The whole label, ignoring case: 'api' does not match 'rapid'. Labels hold no comma.
    private const string LabelCondition = "LOCATE(CONCAT(',', LOWER(@label), ','), CONCAT(',', LOWER(i.labels), ',')) > 0";

    private static long ParseAfter(string? after) =>
        long.TryParse(after, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static void AddWords(List<string> where, DynamicParameters parameters, IReadOnlyList<string> words, string first, string second)
    {
        for (var index = 0; index < words.Count; index++)
        {
            var escaped = words[index].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            where.Add($"({first} LIKE @w{index} OR {second} LIKE @w{index})");
            parameters.Add($"w{index}", $"%{escaped}%");
        }
    }

    public async Task<long> GetChangeHeadAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_changes", cancellationToken: ct)) ?? 0;
    }

    private sealed class ChangeRow
    {
        public long Sequence { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public string EntityKind { get; init; } = "";
        public Guid EntityId { get; init; }
        public string EntityKey { get; init; } = "";
        public string Kind { get; init; } = "";
        public string ActorName { get; init; } = "";
        public string ActorKind { get; init; } = "";
        public string? ActorSessionId { get; init; }
        public string? Harness { get; init; }
        public string? Model { get; init; }
        public string? Host { get; init; }
        public string Changes { get; init; } = "[]";
        public DateTime CreatedAt { get; init; }
    }

    public async Task<IReadOnlyList<ChangeView>> ReadChangesAsync(Guid? projectId, long afterSequence, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // The primary key (or ix_work_changes_project): a range and a limit, never an offset.
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            "SELECT c.sequence, c.project_id, c.project_key, c.entity_kind, c.entity_id, c.entity_key, c.kind, c.actor_name, c.actor_kind, c.actor_session_id, " +
            "COALESCE(s.harness, c.actor_harness) AS harness, COALESCE(s.model, c.actor_model) AS model, COALESCE(s.host, c.actor_host) AS host, c.changes, c.created_at " +
            "FROM work_changes c LEFT JOIN work_sessions s ON s.id = c.actor_session_id WHERE c.sequence > @afterSequence" +
            (projectId is null ? string.Empty : " AND c.project_id = @projectId") +
            " ORDER BY c.sequence LIMIT @limit",
            new { afterSequence, projectId, limit }, cancellationToken: ct));
        return rows.Select(ToView).ToList();
    }

    public async Task<IReadOnlyList<ChangeView>> ReadRecentChangesAsync(Guid projectId, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            "SELECT c.sequence, c.project_id, c.project_key, c.entity_kind, c.entity_id, c.entity_key, c.kind, c.actor_name, c.actor_kind, c.actor_session_id, " +
            "COALESCE(s.harness, c.actor_harness) AS harness, COALESCE(s.model, c.actor_model) AS model, COALESCE(s.host, c.actor_host) AS host, c.changes, c.created_at " +
            "FROM work_changes c LEFT JOIN work_sessions s ON s.id = c.actor_session_id WHERE c.project_id = @projectId ORDER BY c.sequence DESC LIMIT @limit",
            new { projectId, limit = Math.Clamp(limit, 1, WorkLimits.MaxPageSize) }, cancellationToken: ct));
        return rows.Select(ToView).Reverse().ToList();
    }

    public async Task<IReadOnlyList<ChangeView>> ReadEntityChangesAsync(Guid entityId, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            "SELECT c.sequence, c.project_id, c.project_key, c.entity_kind, c.entity_id, c.entity_key, c.kind, c.actor_name, c.actor_kind, c.actor_session_id, " +
            "COALESCE(s.harness, c.actor_harness) AS harness, COALESCE(s.model, c.actor_model) AS model, COALESCE(s.host, c.actor_host) AS host, c.changes, c.created_at " +
            "FROM work_changes c LEFT JOIN work_sessions s ON s.id = c.actor_session_id WHERE c.entity_id = @entityId ORDER BY c.sequence DESC LIMIT @limit",
            new { entityId, limit = Math.Clamp(limit, 1, WorkLimits.MaxPageSize) }, cancellationToken: ct));
        return rows.Select(ToView).Reverse().ToList();
    }

    private static ChangeView ToView(ChangeRow row)
    {
        using var changes = JsonDocument.Parse(row.Changes);
        return new ChangeView(
            row.Sequence, row.ProjectId, row.ProjectKey, ParseName<WorkEntityKind>(row.EntityKind), row.EntityId, row.EntityKey,
            row.Kind, new WorkActor(row.ActorName, row.ActorKind, row.ActorSessionId, row.Harness, row.Model, row.Host),
            changes.RootElement.Clone(), Utc(row.CreatedAt));
    }
}
