using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Services.Work;

/// <summary>Exclude only reads/actions proved ineligible by the server's numeric digest. This is a
/// model-facing catalog projection, never a permission check or a change to canonical tools.</summary>
internal static class DreamToolSelection
{
    public static IReadOnlyList<DreamToolDefinition> Select(IReadOnlyList<DreamToolDefinition> tools, JsonElement digestData,
        long newEntryCount = 0, long newLessonCount = 0, bool unknownNewEntries = false)
    {
        if (unknownNewEntries || newEntryCount < 0 || newLessonCount < 0 || newLessonCount > newEntryCount
            || digestData.ValueKind != JsonValueKind.Object) return tools;
        var signals = digestData.TryGetProperty("signals", out var nested) ? nested : digestData;
        if (signals.ValueKind != JsonValueKind.Object) return tools;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in new[] { "knowledge_count", "lesson_count", "review_evidence_count", "open_challenge_count", "open_proposal_count" })
        {
            if (!signals.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var count) || count < 0) return tools;
            counts[name] = count;
        }
        // An inconsistent snapshot cannot establish absence either.
        if (counts["lesson_count"] > counts["knowledge_count"] || counts["knowledge_count"] > long.MaxValue - newEntryCount
            || counts["lesson_count"] > long.MaxValue - newLessonCount) return tools;
        var knowledgeCount = counts["knowledge_count"] + newEntryCount;
        var lessonCount = counts["lesson_count"] + newLessonCount;
        return tools.Where(tool => tool.Name switch
        {
            "dream_read" => knowledgeCount > 0,
            "dream_lessons" => lessonCount > 0 || counts["review_evidence_count"] > 0,
            "dream_similar" => lessonCount >= 2,
            "dream_verify" or "dream_propose" => knowledgeCount > 0,
            "dream_revise" or "dream_promote" or "dream_challenge" => lessonCount > 0,
            // RelateLessonCoreAsync requires two distinct lessons, not arbitrary entries.
            "dream_relate" => lessonCount >= 2,
            // Autonomous decisions, proposed rules and contradictions all require knowledge
            // entries. Their absence cannot be inferred merely from zero proposals/challenges.
            "dream_review_queue" => knowledgeCount > 0 || counts["open_challenge_count"] > 0 || counts["open_proposal_count"] > 0,
            // Trail's count is watermark-filtered; older complete history remains reachable.
            _ => true
        }).Select(tool => tool.Name == "dream_lessons" ? SelectLessonViews(tool, lessonCount, counts["review_evidence_count"]) : tool).ToArray();
    }

    private static DreamToolDefinition SelectLessonViews(DreamToolDefinition tool, long lessonCount, long reviewCount)
    {
        if (lessonCount > 0 && reviewCount > 0) return tool;
        // The digest's review count covers all project evidence, not just the watermark.
        // Clone only the model projection; every other field and continuation rule survives.
        var schema = JsonNode.Parse(tool.Parameters.GetRawText());
        if (schema?["properties"]?["view"] is not JsonObject view || view["enum"] is not JsonArray values) return tool;
        var eligible = values.Where(value => value is not JsonValue scalar || !scalar.TryGetValue<string>(out var name) || (name switch
        {
            "recurrence" or "unaddressed" or "unmatched" => lessonCount > 0,
            "unlearned_reviews" => reviewCount > 0,
            _ => true
        })).ToArray();
        if (eligible.Length == values.Count) return tool;
        view["enum"] = new JsonArray(eligible.Select(value => value?.DeepClone()).ToArray());
        return tool with { Parameters = JsonSerializer.SerializeToElement(schema) };
    }
}
