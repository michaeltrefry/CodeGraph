using System.Globalization;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// Uses the actual lesson-event rows saved before a direct write and the current lesson events
/// after it. The journal preserves pre-merge event ownership/counting, so merging or unmerging
/// cannot rewrite the baseline. Both entries of a same_as or contradicts operation are inspected.
/// </summary>
public sealed class MySqlDreamingEffectsStore(IOptions<MariaDbStorageOptions> options) : IDreamingEffectsStore
{
    public async Task<DreamEffects?> GetEffectsAsync(Guid projectId, Guid runId, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        // Keep journal and live event reads on one consistent snapshot.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM dreaming_runs WHERE id=@runId AND project_id=@projectId)",
                new { runId, projectId }, transaction, cancellationToken: ct))) return null;
        var journals = await connection.QueryAsync<Journal>(new CommandDefinition(
            "SELECT before_json BeforeJson,after_json AfterJson,created_at CreatedAt FROM dream_write_journal " +
            "WHERE project_id=@projectId AND run_id=@runId ORDER BY id", new { projectId, runId }, transaction, cancellationToken: ct));
        var changes = new List<Change>();
        foreach (var journal in journals)
        {
            using var before = JsonDocument.Parse(journal.BeforeJson);
            using var after = JsonDocument.Parse(journal.AfterJson);
            var previous = Rows(before.RootElement, "knowledge_entries").ToDictionary(row => Text(row, "id"));
            foreach (var entry in Rows(after.RootElement, "knowledge_entries"))
            {
                var id = Text(entry, "id");
                previous.TryGetValue(id, out var old);
                if (Text(entry, "kind") != "lesson" || Text(entry, "rule_state") != "rule" && Text(old, "rule_state") != "rule") continue;
                if (old.ValueKind != JsonValueKind.Undefined && Text(old, "version") == Text(entry, "version")) continue;
                var events = Rows(before.RootElement, "lesson_events").Where(row => Text(row, "entry_id") == id).Select(EventOf).ToList();
                changes.Add(new(Guid.Parse(id), Text(entry, "summary"), Utc(journal.CreatedAt), Count(events, null, Utc(journal.CreatedAt))));
            }
        }
        if (changes.Count == 0) return new DreamEffects([]);
        var live = await connection.QueryAsync<Event>(new CommandDefinition(
            "SELECT entry_id EntryId,event_hash EventHash,correction_id CorrectionId,counted Counted,rule_at_event RuleAtEvent,served Served,created_at CreatedAt " +
            "FROM lesson_events WHERE project_id=@projectId AND entry_id IN @ids ORDER BY created_at,correction_id",
            new { projectId, ids = changes.Select(change => change.Id.ToString()).Distinct().ToArray() }, transaction, cancellationToken: ct));
        var byEntry = live.ToLookup(row => row.EntryId);
        return new DreamEffects(changes.Select(change => new DreamRuleEffect(change.Id, change.Summary, change.At,
            change.Before, Count(byEntry[change.Id], change.At, null))).ToList());
    }

    private static DreamRecurrence Count(IEnumerable<Event> events, DateTime? since, DateTime? until)
    {
        var distinct = events.Where(row => row.Counted).OrderBy(row => row.CreatedAt).ThenBy(row => row.CorrectionId)
            .DistinctBy(row => row.EventHash).ToList();
        bool InWindow(Event row) => (since is null || row.CreatedAt >= since) && (until is null || row.CreatedAt < until);
        // The first correction creates the lesson; it is not a recurrence, matching the Lessons view.
        var recurrences = distinct.Skip(1).Where(InWindow).ToList();
        return new(distinct.Count(InWindow), recurrences.Count(row => row.RuleAtEvent && row.Served == true),
            recurrences.Count(row => row.RuleAtEvent && row.Served == false));
    }
    private static IEnumerable<JsonElement> Rows(JsonElement root, string table) => root.GetProperty("Tables").GetProperty(table).EnumerateArray();
    private static string Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Null ? "" : value.GetRawText() : "";
    private static bool Boolean(JsonElement row, string key) => Text(row, key) is "true" or "1";
    private static Event EventOf(JsonElement row) => new()
    {
        EntryId = Guid.Parse(Text(row, "entry_id")), EventHash = Text(row, "event_hash"), CorrectionId = Guid.Parse(Text(row, "correction_id")),
        Counted = Boolean(row, "counted"), RuleAtEvent = Boolean(row, "rule_at_event"),
        Served = Text(row, "served") == "" ? null : Boolean(row, "served"),
        CreatedAt = DateTime.Parse(Text(row, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
    };
    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);
    private sealed class Journal { public string BeforeJson { get; init; } = ""; public string AfterJson { get; init; } = ""; public DateTime CreatedAt { get; init; } }
    private sealed class Event
    {
        public Guid EntryId { get; init; }
        public string EventHash { get; init; } = "";
        public Guid CorrectionId { get; init; }
        public bool Counted { get; init; }
        public bool RuleAtEvent { get; init; }
        public bool? Served { get; init; }
        public DateTime CreatedAt { get; init; }
    }
    private sealed record Change(Guid Id, string Summary, DateTime At, DreamRecurrence Before);
}
