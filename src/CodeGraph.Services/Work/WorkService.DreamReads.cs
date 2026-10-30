using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService
{
    // Read snapshots live only as long as the active execution scope. No durable cursor/session
    // infrastructure: the existing scope owns their lifetime, project and run capability.
    private static readonly ConditionalWeakTable<DreamExecutionScope, Dictionary<Guid, DreamReadSnapshot>> DreamReads = new();
    public const int DreamReadPageBytes = 12_000;
    private sealed record DreamReadCursor(Guid Run, Guid Project, string Tool, Guid Snapshot, int Item, int Character);
    private sealed record DreamReadSnapshot(string Tool, DateTime CapturedAt, IReadOnlyList<DreamReadItem> Items);
    private sealed class DreamReadItem(Func<CancellationToken, Task<JsonElement>> load, bool cache = true)
    {
        private JsonElement? content;
        public async Task<JsonElement> ReadAsync(CancellationToken ct) => cache ? content ??= await load(ct) : await load(ct);
        public static DreamReadItem Of(JsonElement value) => new(_ => Task.FromResult(value));
    }

    private async Task<object> ReadBoundedDreamToolAsync(string tool, JsonObject input, DreamExecutionScope scope, CancellationToken ct)
    {
        var snapshots = DreamReads.GetOrCreateValue(scope);
        var window = WorkJson.Parse<DreamReadWindow>(JsonSerializer.SerializeToElement(new { limit = input["limit"], cursor = input["cursor"] }),tool);
        var limit = window.Limit ?? 20;
        if (limit is < 1 or > 100) throw WorkException.Invalid("limit must be 1 to 100.");
        DreamReadCursor cursor;
        DreamReadSnapshot snapshot;
        var cursorText = window.Cursor;
        if (cursorText?.StartsWith("dr:", StringComparison.Ordinal) == true)
        {
            RefuseDreamFields(input, "cursor", "limit");
            try { cursor = JsonSerializer.Deserialize<DreamReadCursor>(Convert.FromBase64String(cursorText[3..]))!; }
            catch (Exception ex) when (ex is JsonException or FormatException) { throw WorkException.Invalid("Invalid dream read cursor; pass next_cursor unchanged."); }
            if (cursor is null || cursor.Run != scope.RunId || cursor.Project != scope.ProjectId || cursor.Tool != tool
                || !snapshots.TryGetValue(cursor.Snapshot, out snapshot!) || snapshot.Tool != tool
                || cursor.Item < 0 || cursor.Item >= snapshot.Items.Count || cursor.Character < 0)
                throw WorkException.Invalid("The cursor belongs to another run, project or tool, or is no longer available. Start a read without cursor.");
        }
        else
        {
            // Existing domain readers perform strict argument/filter validation and selection.
            // Collect once, then page that immutable selection instead of re-running mutable
            // offset/ranking queries after every model turn.
            var items = await CaptureDreamReadAsync(tool, input, scope.ProjectId, ct);
            var id = Guid.NewGuid();
            snapshot = new(tool, clock.GetUtcNow().UtcDateTime, items);
            snapshots.Add(id, snapshot);
            cursor = new(scope.RunId, scope.ProjectId, tool, id, 0, 0);
        }
        return await PageDreamReadAsync(snapshot, cursor, limit, ct);
    }

    private sealed record DreamReadWindow(int? Limit=null,string? Cursor=null);

    private async Task<IReadOnlyList<DreamReadItem>> CaptureDreamReadAsync(string tool, JsonObject input, Guid projectId, CancellationToken ct)
    {
        var arguments = JsonSerializer.SerializeToElement(input);
        // Validate the complete initial request before any JsonNode getters or selection.
        // Continuations were validated separately and accept only cursor and limit.
        switch (tool)
        {
            case "dream_read": _ = WorkJson.Parse<RecallRequest>(arguments,tool); break;
            case "dream_lessons": _ = WorkJson.Parse<LessonsRequest>(arguments,tool); break;
            case "dream_review_queue": _ = WorkJson.Parse<ReviewQueueRequest>(arguments,tool); break;
            case "dream_similar":
                RefuseDreamFields(input,"offset","limit");
                _ = WorkJson.Parse<DreamModelContract.PageInput>(arguments,tool); break;
            case "dream_work_trail": _ = WorkJson.Parse<DreamModelContract.PageInput>(arguments,tool); break;
        }
        if (tool == "dream_work_trail")
        {
            RefuseDreamFields(input, "offset", "limit", "since");
            var offset = input["offset"]?.GetValue<int>() ?? 0;
            if (offset < 0) throw WorkException.Invalid("offset must be nonnegative.");
            var rows = await DreamKnowledge.ListDreamWorkTrailAsync(projectId, input["since"]?.GetValue<DateTime?>(), ct);
            return rows.Skip(offset).Select(row => new DreamReadItem(token => DreamKnowledge.ReadDreamWorkTrailItemAsync(projectId, row, token), cache: row.Kind != "revision")).ToList();
        }
        var args = input.DeepClone().AsObject();
        // Input limit controls the bounded output page, not how much history remains reachable.
        var full = args["detail"]?.GetValue<string>() == "full";
        args["limit"] = 50;
        if (tool == "dream_read" && full && !args.ContainsKey("id")) args["detail"] = "summary";
        if (tool == "dream_read" && args.ContainsKey("id")) args.Remove("limit");
        var result = new List<DreamReadItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var page = JsonSerializer.SerializeToElement(await ReadDreamToolPageAsync(tool, args, projectId, ct), WorkJson.Options);
            if (tool == "dream_read" && args.ContainsKey("id"))
            {
                result.Add(DreamReadItem.Of(page));
                break;
            }
            IEnumerable<JsonElement> values = page.TryGetProperty("items", out var items) ? items.EnumerateArray().ToArray()
                : page.TryGetProperty("reviews", out var reviews) ? reviews.EnumerateArray().ToArray()
                : page.TryGetProperty("groups", out var groups) ? groups.EnumerateArray().SelectMany(group => group.GetProperty("lessons").EnumerateArray()).ToArray() : [];
            foreach (var value in values)
            {
                // Unaddressed groups may repeat one lesson for several activities. Its activity
                // list retains the grouping information; a snapshot contains each lesson once.
                var key = value.TryGetProperty("id", out var id) ? id.ToString() : value.GetRawText();
                if (!seen.Add(key)) continue;
                if (tool == "dream_read" && full)
                {
                    var entryId = value.GetProperty("id").GetGuid();
                    var version = value.GetProperty("version").GetInt64();
                    var stale = value.TryGetProperty("possibly_stale", out var flag) && flag.GetBoolean();
                    result.Add(new DreamReadItem(async token =>
                    {
                        var content = JsonNode.Parse((await DreamKnowledge.ReadDreamKnowledgeVersionAsync(projectId, entryId, version, token)).GetRawText())!.AsObject();
                        content["possibly_stale"] = stale;
                        return JsonSerializer.SerializeToElement(content, WorkJson.Options);
                    }, cache: false));
                }
                else result.Add(DreamReadItem.Of(value.Clone()));
            }
            if (page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String)
                args["cursor"] = next.GetString();
            else if (page.TryGetProperty("next_offset", out next) && next.ValueKind == JsonValueKind.Number)
                args["offset"] = next.GetInt32();
            else break;
        }
        return result;
    }

    private static async Task<object> PageDreamReadAsync(DreamReadSnapshot snapshot, DreamReadCursor cursor, int limit, CancellationToken ct)
    {
        string? Next(int item, int character = 0) => item < snapshot.Items.Count
            ? "dr:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor with { Item = item, Character = character })) : null;
        object Page(IReadOnlyList<JsonElement> items, int nextItem, object? fragment = null, int character = 0) => new
        {
            snapshot_at = snapshot.CapturedAt, total_count = snapshot.Items.Count, offset = cursor.Item,
            items, fragment, has_more = nextItem < snapshot.Items.Count, next_cursor = Next(nextItem, character)
        };
        int Size(object value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, WorkJson.Options));
        var result = new List<JsonElement>();
        var index = cursor.Item;
        while (index < snapshot.Items.Count && result.Count < limit)
        {
            var value = await snapshot.Items[index].ReadAsync(ct);
            var candidate = result.Append(value).ToList();
            if (cursor.Character == 0 && Size(Page(candidate, index + 1)) <= DreamReadPageBytes)
            { result.Add(value); index++; continue; }
            if (result.Count > 0) break;
            // A valid body (or a long lesson event/history list) can exceed a whole page.
            // Return exact JSON text fragments, never a truncation without a continuation route.
            var json = value.GetRawText();
            if (cursor.Character >= json.Length) throw WorkException.Invalid("Invalid character offset in dream read cursor.");
            object Fragment(int count)
            {
                var end = cursor.Character + count;
                return Page([], end < json.Length ? index : index + 1,
                    new { format = "json", item_index = index, content = json.Substring(cursor.Character, count),
                        offset_chars = cursor.Character, total_chars = json.Length, has_more = end < json.Length }, end < json.Length ? end : 0);
            }
            var low = 1; var high = json.Length - cursor.Character;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Size(Fragment(middle)) <= DreamReadPageBytes) low = middle; else high = middle - 1;
            }
            // Do not split a surrogate pair; concatenation remains exact Unicode text.
            if (cursor.Character + low < json.Length && char.IsHighSurrogate(json[cursor.Character + low - 1])) low--;
            return Fragment(low);
        }
        return Page(result, index);
    }
}
