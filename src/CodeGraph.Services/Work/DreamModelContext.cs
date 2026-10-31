using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>Model-facing projections only. The canonical contracts and durable run results stay complete.</summary>
internal static class DreamModelContext
{
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<DreamToolDefinition> CompactTools(IReadOnlyList<DreamToolDefinition> tools) =>
        tools.Select(t => t with { Parameters = CompactSchema(t.Parameters) }).ToArray();

    internal static JsonElement CompactSchema(JsonElement original)
    {
        var root = JsonNode.Parse(original.GetRawText())!;
        Visit(root);
        return JsonSerializer.SerializeToElement(root);

        static void Visit(JsonNode? node)
        {
            if (node is not JsonObject schema) return;
            schema.Remove("description"); schema.Remove("examples"); schema.Remove("title");
            // These maps contain property/definition NAMES, not schema keywords. A property
            // literally called description, title or examples must not disappear.
            foreach (var key in new[] { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas" })
                if (schema[key] is JsonObject map) foreach (var child in map) Visit(child.Value);
            foreach (var key in new[] { "items", "additionalItems", "additionalProperties", "contains", "if", "then", "else", "not", "propertyNames", "unevaluatedItems", "unevaluatedProperties" })
            {
                if (schema[key] is JsonArray tuple) foreach (var child in tuple) Visit(child);
                else Visit(schema[key]);
            }
            foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
                if (schema[key] is JsonArray children) foreach (var child in children) Visit(child);
        }
    }

    public static string RuntimeBudget(DreamSettings settings, int turn, long spent, long remaining) => $"""

        ## Server runtime budget
        Turn {turn} of {settings.MaxTurns}. Total run limit: {settings.MaxTokensPerRun} tokens; already charged: {spent}; remaining: {remaining}.
        This balance pays for ALL replayed input, tool schemas and output, not just new text. A source file or broad history page can make the next turn unaffordable.
        Select concrete work from relevant digest signals, batch independent reads, and act or report before fetching another unrelated page. Do not visit empty views as a checklist. Summary previews select work; they do not substantiate claims. Inspect full evidence for selected work. You need not exhaust every category or pagination cursor in one pass; report uninspected coverage honestly, without treating it as reviewed or clear.
        Leave enough balance for a final response. When the remaining balance is smaller than the recent input, finish a concise report instead of starting another investigation. {((turn == settings.MaxTurns) ? "This is the last allowed turn: finish the report now." : "")}

        ## Shared tool argument conventions
        Use the complete published argument shapes. IDs are stored UUIDs, not item keys or paths. expected_version is the current target version; reread after conflict.
        Direct-write evidence uses kind/reference/summary. lesson_event references correction_id; review references GitHub evidence; thread references item comment; graph_node is a positive numeric node ID encoded as a string, never a file path. Other references are UUIDs returned by scoped readers. For file verification cite the entry and explain the checked path/behavior; actual same-run source/hash proof is still required. Prevalence states observed events/sessions/items and unavailable counts honestly.
        Proposals use kind/id citations (review_evidence, not review); put explanations in rationale. Drafts require actual recurrence while the rule was served. Never copy example identifiers or turn a refusal into a workaround.
        """;

    // Tool data is sent as JSON TEXT to a model, never embedded as HTML. Avoid HTML-style
    // \u0022/\u003C escaping of source and Unicode while preserving exact JSON values.
    public static string ModelJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions)) Write(value, writer);
        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject()) { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var child in element.EnumerateArray()) Write(child, writer); writer.WriteEndArray(); break;
                case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
                default: element.WriteTo(writer); break;
            }
        }
    }

    public static int CompactVerifiedSources(List<DreamModelMessage> messages, string tool, JsonElement result)
    {
        if (tool != "dream_verify" || !result.TryGetProperty("journal_id", out var journal) || journal.ValueKind != JsonValueKind.Number || !journal.TryGetInt64(out var journalId) || journalId <= 0
            || !result.TryGetProperty("data", out var data) || !data.TryGetProperty("entry", out var entry)
            || !entry.TryGetProperty("id", out var entryId) || !entry.TryGetProperty("verified_files", out var files) || files.ValueKind != JsonValueKind.Array) return 0;
        var verified = files.EnumerateArray().Where(f => f.TryGetProperty("repository", out _) && f.TryGetProperty("path", out _))
            .Select(f => FileKey(f.GetProperty("repository").GetString()!, f.GetProperty("path").GetString()!)).ToHashSet(StringComparer.Ordinal);
        var sourceCalls = messages.Where(m => m.Role == "assistant").SelectMany(m => m.ToolCalls ?? [])
            .Where(c => c.Name == "dream_read_source").Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != "tool" || message.ToolCallId is null || !sourceCalls.Contains(message.ToolCallId) || message.Content is null) continue;
            using var document = JsonDocument.Parse(message.Content);
            if (!document.RootElement.TryGetProperty("untrusted_data", out var source) || source.ValueKind != JsonValueKind.Object
                || !source.TryGetProperty("source", out var content) || content.ValueKind != JsonValueKind.String
                || !source.TryGetProperty("repository", out var repository) || !source.TryGetProperty("path", out var path)
                || !verified.Contains(FileKey(repository.GetString()!, path.GetString()!))) continue;
            var receipt = JsonNode.Parse(source.GetRawText())!.AsObject();
            receipt.Remove("source");
            receipt["source_chars"] = content.GetString()!.Length;
            receipt["compacted_after"] = JsonSerializer.SerializeToNode(new { action = "dream_verify", entry_id = entryId.GetString(), journal_id = journalId });
            receipt["full_result_in_run_report"] = true;
            receipt["reread"] = JsonSerializer.SerializeToNode(new { tool = "dream_read_source", arguments = new { repository = repository.GetString(), path = path.GetString() } });
            var compact = ModelJson(JsonSerializer.SerializeToElement(new { untrusted_data = receipt }));
            removed += Math.Max(0, Encoding.UTF8.GetByteCount(message.Content) - Encoding.UTF8.GetByteCount(compact));
            messages[i] = message with { Content = compact };
        }
        return removed;
    }

    private static string FileKey(string repository, string path) => repository.ToLowerInvariant() + ":" + path.Replace('\\', '/');
}
