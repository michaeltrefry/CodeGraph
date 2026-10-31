using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService
{
    // Only snapshots explicitly captured/restored by the host are capabilities in this scope.
    // Serializable descriptors retain lazy loading; checkpointing never reads unseen bodies.
    private static readonly ConditionalWeakTable<DreamExecutionScope, Dictionary<Guid, DreamReadSnapshot>> DreamReads = new();
    public const int DreamReadPageBytes = 12_000;
    private sealed record DreamReadCursor(Guid Run, Guid Project, string Tool, Guid Snapshot, int Item, int Character);
    private sealed record DreamReadSnapshot(string Tool, Guid OriginRunId, DateTime CapturedAt, IReadOnlyList<DreamReadItem> Items);
    private sealed record DreamReadWindowsState(int Version, Guid ProjectId, Guid OwnerRunId, IReadOnlyList<DreamReadSnapshotState> Windows);
    private sealed record DreamReadSnapshotState(Guid Id, string Tool, Guid OriginRunId, DateTime CapturedAt, IReadOnlyList<DreamReadItemState> Items);
    private sealed record DreamReadTrailState(string Kind, string Id, DateTime CreatedAt, string ContentHash, Guid? ItemId = null, string? Preview = null, bool PreviewTruncated = false)
    {
        public DreamTrailReference Reference() => new(Kind, Id, ItemId, CreatedAt, ContentHash, Preview, PreviewTruncated);
    }
    private sealed record DreamReadItemState(string Kind, bool Cache, string? Json = null, Guid? EntryId = null,
        long? EntryVersion = null, bool? PossiblyStale = null, DreamReadTrailState? Trail = null);
    private static bool ReadWindowTool(string tool) => tool is "dream_read" or "dream_lessons" or "dream_similar" or "dream_review_queue" or "dream_work_trail";
    private sealed class DreamReadItem(DreamReadItemState descriptor)
    {
        private JsonElement? content = descriptor.Json is { } json ? JsonSerializer.Deserialize<JsonElement>(json) : null;
        public DreamReadItemState Capture() => descriptor with { Json = content?.GetRawText() };
        public async Task<JsonElement> ReadAsync(WorkService service, Guid projectId, CancellationToken ct)
        {
            if (descriptor.Cache && content is { } cached) return cached;
            JsonElement value;
            if (descriptor.Kind == "entry_version")
            {
                var body = await service.DreamKnowledge.ReadDreamKnowledgeVersionAsync(projectId, descriptor.EntryId!.Value, descriptor.EntryVersion!.Value, ct);
                var node = JsonNode.Parse(body.GetRawText())!.AsObject();
                node["possibly_stale"] = descriptor.PossiblyStale!.Value;
                value = JsonSerializer.SerializeToElement(node, WorkJson.Options);
            }
            else if (descriptor.Kind == "trail") value = await service.DreamKnowledge.ReadDreamWorkTrailItemAsync(projectId, descriptor.Trail!.Reference(), ct);
            else throw WorkException.Invalid("The read-window item has no available value.");
            if (descriptor.Cache) content = value.Clone();
            return value;
        }
        public static DreamReadItem Of(JsonElement value) => new(new("inline", true, value.GetRawText()));
        public static DreamReadItem Entry(Guid id, long version, bool stale) => new(new("entry_version", false, EntryId: id, EntryVersion: version, PossiblyStale: stale));
        public static DreamReadItem Trail(DreamTrailReference row) => new(new("trail", row.Kind != "revision",
            Trail: new(row.Kind, row.Id, row.CreatedAt, row.ContentHash, row.ItemId, row.Preview, row.PreviewTruncated)));
    }

    public JsonElement CaptureReadWindows(DreamExecutionScope scope)
    {
        RequireReadWindowScope(scope);
        var snapshots = DreamReads.GetOrCreateValue(scope);
        return JsonSerializer.SerializeToElement(new DreamReadWindowsState(1, scope.ProjectId, scope.RunId,
            snapshots.Select(pair => new DreamReadSnapshotState(pair.Key, pair.Value.Tool, pair.Value.OriginRunId, pair.Value.CapturedAt,
                pair.Value.Items.Select(item => item.Capture()).ToList())).ToList()), WorkJson.Options);
    }

    public void RestoreReadWindows(DreamExecutionScope scope, JsonElement state)
    {
        RequireReadWindowScope(scope);
        if (state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || state.ValueKind == JsonValueKind.Object && !state.EnumerateObject().Any()) return;
        var restored = WorkJson.Parse<DreamReadWindowsState>(state, "restore_read_windows");
        if (restored.Windows is null || restored.Version != 1 || restored.ProjectId != scope.ProjectId || restored.OwnerRunId == Guid.Empty
            || scope.ResumeFromRunId is null || scope.ResumeFromRunId != restored.OwnerRunId)
            throw WorkException.Invalid("Read-window state does not belong to the authorized prior run and current project, or its version is unsupported.");
        var snapshots = new Dictionary<Guid, DreamReadSnapshot>();
        foreach (var window in restored.Windows)
        {
            if (window is null || window.Items is null || window.Id == Guid.Empty || window.OriginRunId == Guid.Empty || !ReadWindowTool(window.Tool)
                || snapshots.ContainsKey(window.Id)) throw WorkException.Invalid("Invalid read-window identity or tool.");
            foreach (var item in window.Items) ValidateReadWindowItem(window.Tool, item);
            snapshots.Add(window.Id, new(window.Tool, window.OriginRunId, window.CapturedAt, window.Items.Select(item => new DreamReadItem(item)).ToList()));
        }
        // Validate everything before publishing any restored capability. A second restore
        // must not replace live windows (and invalidate cursors the caller already received).
        lock (DreamReads)
        {
            if (DreamReads.TryGetValue(scope, out var existing) && existing.Count > 0)
                throw new WorkException(WorkErrorCodes.Conflict, "This scope already has read windows; restore only into a fresh continuation scope.");
            DreamReads.Remove(scope);
            DreamReads.Add(scope, snapshots);
        }
    }

    private static void RequireReadWindowScope(DreamExecutionScope scope)
    {
        if (scope is null || !ReferenceEquals(scope, DreamExecutionScope.Current) || scope.ProjectId == Guid.Empty || scope.RunId == Guid.Empty)
            throw new WorkException(WorkErrorCodes.Forbidden, "Read windows require the active execution scope and project.");
        DreamPolicy.RequireCurrentProject(scope.ProjectId);
    }

    private static void ValidateReadWindowItem(string tool, DreamReadItemState item)
    {
        if (item is null) throw WorkException.Invalid("Invalid null read-window item.");
        var valid = item.Kind switch
        {
            "inline" => item.Cache && item.Json is not null && item.EntryId is null && item.EntryVersion is null && item.PossiblyStale is null && item.Trail is null,
            "entry_version" => tool == "dream_read" && !item.Cache && item.Json is null && item.EntryId is { } id && id != Guid.Empty
                && item.EntryVersion is > 0 && item.PossiblyStale is not null && item.Trail is null,
            "trail" => tool == "dream_work_trail" && item.EntryId is null && item.EntryVersion is null && item.PossiblyStale is null
                && item.Trail is { } trail && trail.Kind is "revision" or "attempt" or "handoff" or "thread" or "message" or "review"
                && item.Cache == (trail.Kind != "revision") && (item.Cache || item.Json is null)
                && !string.IsNullOrWhiteSpace(trail.Id) && trail.ContentHash is { Length: 64 } && trail.ContentHash.All(Uri.IsHexDigit),
            _ => false
        };
        if (!valid) throw WorkException.Invalid("Invalid read-window item descriptor.");
        if (item.Json is { } json)
        {
            try { using var value = JsonDocument.Parse(json); }
            catch (JsonException) { throw WorkException.Invalid("Invalid cached JSON in read-window state."); }
        }
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
            if (cursor is null || cursor.Project != scope.ProjectId || cursor.Tool != tool
                || !snapshots.TryGetValue(cursor.Snapshot, out snapshot!) || snapshot.Tool != tool
                || cursor.Run != snapshot.OriginRunId
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
            snapshot = new(tool, scope.RunId, clock.GetUtcNow().UtcDateTime, items);
            snapshots.Add(id, snapshot);
            cursor = new(scope.RunId, scope.ProjectId, tool, id, 0, 0);
        }
        return await PageDreamReadAsync(scope.ProjectId, snapshot, cursor, limit, ct);
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
            RefuseDreamFields(input, "offset", "limit", "since", "kind", "id", "item", "detail", "expected_hash");
            var request = WorkJson.Parse<DreamModelContract.PageInput>(arguments, tool);
            if (request.Offset < 0) throw WorkException.Invalid("offset must be nonnegative.");
            if (request.Kind is not null && request.Kind is not ("revision" or "attempt" or "handoff" or "thread" or "message" or "review"))
                throw WorkException.Invalid("kind must be revision, attempt, handoff, thread, message or review.");
            if (request.Detail is not null && request.Detail is not ("summary" or "full"))
                throw WorkException.Invalid("detail must be summary or full.");
            if (request.Item == Guid.Empty) throw WorkException.Invalid("item must be a nonempty work item UUID.");
            if (request.ExpectedHash is { } expectedHash && (request.Id is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
                throw WorkException.Invalid("expected_hash requires id and a 64-digit hexadecimal content hash from a summary row.");
            if (request.Id is { } id)
            {
                var parts = id.Split(':');
                var revisionId = parts.Length == 2 && Guid.TryParse(parts[0], out var entry) && entry != Guid.Empty
                    && int.TryParse(parts[1], out var revision) && revision > 0;
                var ordinaryId = Guid.TryParse(id, out var identifier) && identifier != Guid.Empty;
                if (!(request.Kind == "revision" ? revisionId : request.Kind is not null ? ordinaryId : revisionId || ordinaryId))
                    throw WorkException.Invalid("id must be a stored UUID, or entry UUID:positive revision for a revision row.");
            }
            var rows = await DreamKnowledge.QueryDreamWorkTrailAsync(projectId, request.Since, request.Kind, request.Id, request.Item, ct);
            if (request.Id is not null && rows.Count == 0) throw WorkException.NotFound("No work-trail row matches this project's selection.");
            if (request.ExpectedHash is { } hash && rows.Any(row => !row.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new WorkException(WorkErrorCodes.Conflict, "This work-trail row changed after discovery. Start a new summary read before judging its current content.");
            var fullTrail = request.Detail == "full" || request.Detail is null && request.Id is not null;
            IEnumerable<DreamTrailReference> ordered = fullTrail ? rows : rows.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ThenBy(row => row.Kind, StringComparer.Ordinal);
            return ordered.Skip(request.Offset).Select(row => fullTrail
                ? DreamReadItem.Trail(row)
                : DreamReadItem.Of(JsonSerializer.SerializeToElement(new
                {
                    row.Kind, row.Id, row.ItemId, row.CreatedAt, row.ContentHash, row.Preview, preview_truncated = row.PreviewTruncated,
                    detail = "summary", read = new { tool = "dream_work_trail", arguments = new { kind = row.Kind, id = row.Id, detail = "full", expected_hash = row.ContentHash } }
                }, WorkJson.Options))).ToList();
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
                    result.Add(DreamReadItem.Entry(entryId, version, stale));
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

    private async Task<object> PageDreamReadAsync(Guid projectId, DreamReadSnapshot snapshot, DreamReadCursor cursor, int limit, CancellationToken ct)
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
            var value = await snapshot.Items[index].ReadAsync(this, projectId, ct);
            var candidate = result.Append(value).ToList();
            if (cursor.Character == 0 && Size(Page(candidate, index + 1)) <= DreamReadPageBytes)
            { result.Add(value); index++; continue; }
            if (result.Count > 0) break;
            // A valid body (or a long lesson event/history list) can exceed a whole page.
            // Return exact JSON text fragments, never a truncation without a continuation route.
            var json = value.GetRawText();
            if (cursor.Character >= json.Length) throw WorkException.Invalid("Invalid character offset in dream read cursor.");
            if (cursor.Character > 0 && char.IsLowSurrogate(json[cursor.Character]) && char.IsHighSurrogate(json[cursor.Character - 1]))
                throw WorkException.Invalid("Invalid character boundary in dream read cursor.");
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
