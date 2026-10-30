using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore : IDreamingKnowledgeStore
{
    // Explicit allowlist: journal material is never executable SQL or an arbitrary table name.
    private static readonly string[] DreamSnapshotTables = ["knowledge_entries", "knowledge_entry_repositories", "knowledge_entry_paths", "knowledge_entry_nodes", "knowledge_entry_files", "lesson_events", "lesson_links", "lesson_challenges"];
    private sealed record DreamSnapshot(Guid[] EntryIds, Dictionary<string, List<Dictionary<string, JsonElement>>> Tables);
    private sealed class DreamJournalRow
    {
        public long Id { get; init; }
        public Guid EntryId { get; init; }
        public string Action { get; init; } = "";
        public string BeforeJson { get; init; } = "";
        public string AfterJson { get; init; } = "";
        public DateTime? RevertedAt { get; init; }
    }

    private static string DreamWhere(string table) => table switch
    {
        "knowledge_entries" => "id IN @ids",
        "lesson_links" => "entry_id IN @ids OR other_id IN @ids",
        _ => "entry_id IN @ids"
    };

    private static async Task<DreamSnapshot?> CaptureDreamBeforeAsync(MySqlConnection connection, MySqlTransaction transaction, WorkActor actor, Guid[] entryIds, CancellationToken ct)
    {
        if (actor.Kind != WorkActor.SystemKind || actor.Name != "dreamer") return null;
        if (DreamExecutionScope.Current is not { } scope) throw WorkException.Invalid("A dreamer write requires its active run.");
        var projectIds = (await connection.QueryAsync<Guid>(new CommandDefinition("SELECT DISTINCT project_id FROM knowledge_entries WHERE id IN @ids", new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct))).ToArray();
        if (projectIds.Any(project => project != scope.ProjectId)) throw WorkException.Invalid("Dream writes stay within one project.");
        return await CaptureDreamSnapshotAsync(connection, transaction, entryIds, ct);
    }

    private static async Task<DreamSnapshot> CaptureDreamSnapshotAsync(MySqlConnection connection, MySqlTransaction transaction, Guid[] entryIds, CancellationToken ct)
    {
        var tables = new Dictionary<string, List<Dictionary<string, JsonElement>>>();
        foreach (var table in DreamSnapshotTables)
        {
            var rows = await connection.QueryAsync(new CommandDefinition($"SELECT * FROM {table} WHERE {DreamWhere(table)}", new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct));
            tables[table] = rows.Select(row => ((IDictionary<string, object>)row).ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))).ToList();
        }
        return new DreamSnapshot(entryIds, tables);
    }

    private static async Task JournalDreamAfterAsync(MySqlConnection connection, MySqlTransaction transaction, WorkActor actor, DreamSnapshot? before, string action, CancellationToken ct)
    {
        if (before is null) return;
        var scope = DreamExecutionScope.Current!;
        var after = await CaptureDreamSnapshotAsync(connection, transaction, before.EntryIds, ct);
        if (SnapshotComparable(before) == SnapshotComparable(after)) return;
        if (string.IsNullOrWhiteSpace(DreamWriteMetadata.Prevalence)) throw WorkException.Invalid("A dream write requires prevalence.");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO dream_write_journal (run_id,project_id,entry_id,action,before_json,after_json,evidence_json,prevalence,created_at) VALUES (@run,@project,@entry,@action,@before,@after,@evidence,@prevalence,UTC_TIMESTAMP(6))",
            new { run = scope.RunId, project = scope.ProjectId, entry = before.EntryIds[0], action, before = JsonSerializer.Serialize(before), after = JsonSerializer.Serialize(after), evidence = JsonSerializer.Serialize(scope.Evidence, WorkJson.Options), prevalence = DreamWriteMetadata.Prevalence }, transaction, cancellationToken: ct));
    }

    private static string SnapshotComparable(DreamSnapshot snapshot) => JsonSerializer.Serialize(snapshot.Tables.OrderBy(pair => pair.Key).Select(pair => new
    {
        table = pair.Key,
        rows = pair.Value.Select(row => JsonSerializer.Serialize(row.Where(field => pair.Key != "knowledge_entries" || field.Key is not ("version" or "revision" or "updated_at")).OrderBy(field => field.Key).ToDictionary(field => field.Key, field => field.Value))).Order(StringComparer.Ordinal).ToArray()
    }));

    public Task<IReadOnlyList<DreamRestoreEntry>> RevertDreamWritesAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([LessonMergeLockName], async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await HoldProjectAsync(connection, transaction, projectId, ct);
            // Holding the project serializes revert with all direct lesson writers. Each entry is
            // also locked, so standalone verify/revise cannot race the conflict check.
            var journals = (await connection.QueryAsync<DreamJournalRow>(new CommandDefinition("SELECT id,entry_id,action,before_json,after_json,reverted_at FROM dream_write_journal WHERE project_id=@projectId AND run_id=@runId ORDER BY id DESC FOR UPDATE", new { projectId, runId }, transaction, cancellationToken: ct))).ToList();
            var results = new List<DreamRestoreEntry>();
            var restoredVersions = new Dictionary<Guid, long>();
            foreach (var journal in journals)
            {
                if (journal.RevertedAt is not null) { results.Add(new(journal.Id, journal.EntryId, journal.Action, "already_reverted")); continue; }
                var before = JsonSerializer.Deserialize<DreamSnapshot>(journal.BeforeJson)!;
                var after = JsonSerializer.Deserialize<DreamSnapshot>(journal.AfterJson)!;
                await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM knowledge_entries WHERE id IN @ids ORDER BY id FOR UPDATE", new { ids = after.EntryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct));
                var current = await CaptureDreamSnapshotAsync(connection, transaction, after.EntryIds, ct);
                var versionConflict = current.Tables["knowledge_entries"].Any(row =>
                {
                    var id = Guid.Parse(row["id"].GetString()!);
                    var expected = restoredVersions.TryGetValue(id, out var restored) ? restored : after.Tables["knowledge_entries"].Single(saved => saved["id"].GetString() == row["id"].GetString())["version"].GetInt64();
                    return row["version"].GetInt64() != expected;
                });
                if (versionConflict || SnapshotComparable(current) != SnapshotComparable(after))
                {
                    results.Add(new(journal.Id, journal.EntryId, journal.Action, "conflict", "An affected entry or its lesson evidence changed after this write; nothing was restored.")); continue;
                }
                if (journal.Action == "same_as")
                {
                    var version = current.Tables["knowledge_entries"].Single(row => row["id"].GetString() == journal.EntryId.ToString())["version"].GetInt64();
                    await UnmergeLessonCoreAsync(connection, transaction, journal.EntryId, version, actor, Now(), ct);
                }
                else
                {
                    await RestoreDreamSnapshotAsync(connection, transaction, before, current, actor, ct);
                }
                foreach (var id in after.EntryIds)
                {
                    var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT version FROM knowledge_entries WHERE id=@id", new { id }, transaction, cancellationToken: ct));
                    restoredVersions[id] = version;
                }
                await connection.ExecuteAsync(new CommandDefinition("UPDATE dream_write_journal SET reverted_at=@now,reverted_by=@by WHERE id=@id", new { now = Now(), by = actor.Name, id = journal.Id }, transaction, cancellationToken: ct));
                results.Add(new(journal.Id, journal.EntryId, journal.Action, "reverted"));
            }
            await transaction.CommitAsync(ct);
            return (IReadOnlyList<DreamRestoreEntry>)results;
        }, ct);

    private async Task RestoreDreamSnapshotAsync(MySqlConnection connection, MySqlTransaction transaction, DreamSnapshot before, DreamSnapshot current, WorkActor actor, CancellationToken ct)
    {
        var now = Now();
        foreach (var entry in current.Tables["knowledge_entries"])
        {
            var id = Guid.Parse(entry["id"].GetString()!);
            var old = before.Tables["knowledge_entries"].SingleOrDefault(row => row["id"].GetString() == id.ToString());
            var version = entry["version"].GetInt64() + 1;
            var revision = entry["revision"].GetInt32() + 1;
            if (old is null)
            {
                // A newly recorded entry remains in history, with a retraction as its inverse.
                await connection.ExecuteAsync(new CommandDefinition("UPDATE knowledge_entries SET status='retracted',status_reason='Reverted dreaming run',version=@version,revision=@revision,updated_at=@now WHERE id=@id", new { id, version, revision, now }, transaction, cancellationToken: ct));
            }
            else
            {
                var parameters = new DynamicParameters();
                var assignments = new List<string>();
                foreach (var (column, value) in old.Where(pair => pair.Key is not ("id" or "version" or "revision" or "updated_at")))
                {
                    var name = "p" + assignments.Count;
                    assignments.Add($"`{column}`=@{name}"); parameters.Add(name, DreamSqlValue(column, value));
                }
                parameters.Add("id", id); parameters.Add("version", version); parameters.Add("revision", revision); parameters.Add("now", now);
                await connection.ExecuteAsync(new CommandDefinition($"UPDATE knowledge_entries SET {string.Join(',', assignments)},version=@version,revision=@revision,updated_at=@now WHERE id=@id", parameters, transaction, cancellationToken: ct));
            }
        }
        // New records retain their provenance; all existing entries recover their exact scopes,
        // freshness baselines, relations and challenges. Revisions are always append-only.
        var existingIds = before.Tables["knowledge_entries"].Select(row => row["id"].GetString()!).ToArray();
        if (existingIds.Length > 0)
        {
            foreach (var table in DreamSnapshotTables.Skip(1))
            {
                await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE {DreamWhere(table)}", new { ids = existingIds }, transaction, cancellationToken: ct));
                foreach (var row in before.Tables[table])
                {
                    var parameters = new DynamicParameters();
                    var fields = row.ToArray();
                    for (var index = 0; index < fields.Length; index++) parameters.Add("p" + index, DreamSqlValue(fields[index].Key, fields[index].Value));
                    await connection.ExecuteAsync(new CommandDefinition($"INSERT INTO {table} ({string.Join(',', fields.Select(field => "`" + field.Key + "`"))}) VALUES ({string.Join(',', fields.Select((_, index) => "@p" + index))})", parameters, transaction, cancellationToken: ct));
                }
            }
        }
        foreach (var entry in current.Tables["knowledge_entries"])
        {
            var id = Guid.Parse(entry["id"].GetString()!);
            await InsertRevisionAsync(connection, transaction, id, entry["revision"].GetInt32() + 1, "dream_reverted", actor, now, ct);
        }
    }

    private static object? DreamSqlValue(string column, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDecimal(),
        JsonValueKind.String when column.EndsWith("_at", StringComparison.Ordinal) || column == "rule_since" => DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        JsonValueKind.String => value.GetString(),
        _ => throw WorkException.Invalid("Invalid journal field type.")
    };
}
