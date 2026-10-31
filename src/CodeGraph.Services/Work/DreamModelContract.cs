using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Services.Work;

/// <summary>The private model API omits server identity and describes policy restrictions
/// on the wider public work contracts. Keep public MCP/REST requests unchanged.</summary>
internal static class DreamModelContract
{
    internal sealed record PageInput(int Offset=0,int Limit=20,DateTime? Since=null,string? Cursor=null,
        string? Kind=null,string? Id=null,Guid? Item=null,string? Detail=null,string? ExpectedHash=null);
    internal sealed record ChallengeInput(string Id,string Reason);
    internal sealed record SearchInput(string Repository,string NamePattern="%",string? Label=null,int Limit=20);
    internal sealed record SourceInput(string? Repository=null,string? Path=null,long? NodeId=null,string Detail="auto");

    private const string ExampleId = "d915b0cc-f63c-4fb6-9fca-6de2af91f062";
    private static readonly string[] EvidenceKinds = ["entry", "lesson_event", "item", "challenge", "message", "graph_node", "review", "attempt", "handoff", "thread"];

    internal static JsonObject TextSchema(string description, int maximum) => new()
    { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximum, ["description"] = description };

    internal static JsonObject EvidenceSchema() => new()
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 10,
        ["items"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = Names("kind", "reference", "summary"),
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Names(EvidenceKinds) },
                ["reference"] = TextSchema("Real stored identifier: entry=knowledge entry UUID; lesson_event=correction_id UUID; item=work item UUID (not key); challenge=challenge UUID; message=message UUID; graph_node=positive numeric node ID encoded as a string (never a file path); review=GitHub evidence UUID; attempt=attempt UUID; handoff=handoff UUID; thread=item comment UUID. Use IDs returned by scoped readers.", 128),
                ["summary"] = TextSchema("What this evidence establishes for this action. For source verification cite the existing entry and describe the repository/path and behavior checked. Actual source reads and matching hashes are still required; source is not an evidence kind.", 1000)
            },
            ["examples"] = new JsonArray(
                new JsonObject { ["kind"] = "entry", ["reference"] = ExampleId, ["summary"] = "Checked codegraph/src/Example.cs; current behavior still supports the entry." },
                new JsonObject { ["kind"] = "graph_node", ["reference"] = "123", ["summary"] = "Graph node 123 identifies the renamed method read this run." })
        }
    };

    internal static JsonElement Complete(string tool, JsonElement original)
    {
        var root = JsonNode.Parse(original.GetRawText())!.AsObject();
        var properties = root["properties"]!.AsObject();
        if (properties["limit"] is JsonObject limit) { limit["minimum"] = 1; limit["maximum"] = tool == "dream_search_graph" ? 50 : 100; }
        if (properties["offset"] is JsonObject offset) offset["minimum"] = 0;
        if (properties["id"] is JsonObject id) id["description"] = "Knowledge entry UUID from dream_read; never an item key or source path.";
        if (properties["expected_version"] is JsonObject version) version["description"] = "Current target version returned by dream_read. Reread after conflict.";
        switch (tool)
        {
            case "dream_read_prior":
                properties["action_index"]!["minimum"]=1;properties["offset_chars"]!["minimum"]=0;
                break;
            case "dream_work_trail":
                properties["kind"]!["enum"] = new JsonArray("revision", "attempt", "handoff", "thread", "message", "review", (JsonNode?)null);
                properties["detail"]!["enum"] = new JsonArray("summary", "full", (JsonNode?)null);
                break;
            case "dream_lessons":
                // A continuation has only cursor/limit: View is required for the first page,
                // but asking the model to repeat it would violate the executor's cursor API.
                root["required"] = Names();
                root["anyOf"] = new JsonArray(new JsonObject { ["required"] = Names("view") }, new JsonObject { ["required"] = Names("cursor") });
                properties["cursor"]!["type"] = "string";
                properties["view"]!["enum"] = Names("recurrence", "unaddressed", "unmatched", "unlearned_reviews");
                break;
            case "dream_revise":
                Require(root, "applies_when");
                properties["applies_when"]!["type"] = "object";
                break;
            case "dream_relate":
                properties["same_as"]!["type"] = "string";
                properties["contradicts"]!["type"] = "string";
                root["oneOf"] = new JsonArray(
                    new JsonObject { ["required"] = Names("same_as"), ["not"] = new JsonObject { ["required"] = Names("contradicts") } },
                    new JsonObject { ["required"] = Names("contradicts"), ["not"] = new JsonObject { ["required"] = Names("same_as") } });
                break;
            case "dream_record":
                Require(root, "entry");
                var entry = properties["entry"]!.AsObject();
                entry["type"] = "object";
                RestrictEntry(entry);
                foreach (var field in new[] { "why", "alternatives", "context", "consequences", "made_autonomously" }) entry["properties"]!.AsObject().Remove(field);
                Require(entry, "confidence");
                entry["properties"]!["kind"]!["enum"] = Names("fact", "gotcha", "constraint", "attempt", "lesson");
                entry["properties"]!["confidence"]!["type"] = "string";
                entry["properties"]!["confidence"]!["enum"] = Names("high", "medium", "low");
                entry["properties"]!["source"]!["properties"]!["kind"]!["enum"] = Names("agent");
                entry["properties"]!["source"]!["properties"]!["kind"]!["description"] = "Dreamer lessons must use agent; no other correction source is permitted.";
                entry["anyOf"] = new JsonArray(KindBranch(["fact", "gotcha", "constraint"]), KindBranch(["attempt"], "why_failed"), KindBranch(["lesson"], "source"));
                break;
            case "dream_propose":
                Require(root, "action", "target", "expected_version", "rationale", "prevalence", "evidence");
                properties["target"]!["description"] = "Active knowledge entry UUID returned by dream_read in this project. Server supplies project and run_id: do not pass identity fields.";
                properties["rationale"] = TextSchema("Why this action is justified, with confidence and uncertainties.", 1000);
                properties["prevalence"] = TextSchema("Observed counts, distinct sessions/items and time range; state unavailable counts explicitly.", 1000);
                var evidence = properties["evidence"]!.AsObject();
                evidence["minItems"] = 1; evidence["maxItems"] = 50;
                var citation = evidence["items"]!.AsObject();
                Require(citation, "kind", "id");
                citation["properties"]!["id"]!["description"] = "Real stored UUID for entry, lesson_event (correction_id), item or review_evidence (GitHub evidence); graph_node uses a positive numeric node ID encoded as a string, never a source path. Unlike direct-write evidence this citation uses id, with the explanation in rationale.";
                citation["examples"] = new JsonArray(new JsonObject { ["kind"] = "entry", ["id"] = ExampleId }, new JsonObject { ["kind"] = "graph_node", ["id"] = "123" });
                RestrictEntry(properties["replacement"]!.AsObject());
                properties["replacement"]!["type"] = "object";
                properties["replacement"]!["anyOf"] = new JsonArray(KindBranch(["goal", "constraint", "fact", "gotcha", "architecture"]),
                    KindBranch(["decision"]), KindBranch(["attempt"], "why_failed"), KindBranch(["lesson"]));
                properties["other"]!["type"] = "string";
                properties["other_expected_version"]!["type"] = "integer";
                properties["draft"] = TextSchema("Reviewable skill, hook or test text for external application, required only for draft actions.", 100000);
                root["anyOf"] = new JsonArray(
                    ActionBranch("retract"), ActionBranch("supersede", "replacement"), ActionBranch("merge", "other", "other_expected_version"),
                    ActionBranch("skill_draft", "draft"), ActionBranch("hook_draft", "draft"), ActionBranch("test_draft", "draft"));
                break;
            case "dream_search_graph":
                Require(root, "repository");
                properties["label"]!["enum"] = new JsonArray(Enum.GetNames<CodeGraph.Models.NodeLabel>().Select(name => (JsonNode?)JsonValue.Create(name)).Append(null).ToArray());
                break;
            case "dream_challenge": Require(root, "id", "reason"); break;
            case "dream_read_source":
                properties["detail"]!["enum"] = Names("auto", "full");
                properties["repository"]!["type"] = "string";
                properties["path"]!["type"] = "string";
                properties["node_id"]!["type"] = "integer";
                properties["node_id"]!["minimum"] = 1;
                properties["node_id"]!["description"] = "Positive numeric graph ID returned by dream_search_graph. Reads the node's actual file, or the graph metadata if it has no file.";
                root["anyOf"] = new JsonArray(new JsonObject { ["required"] = Names("node_id") }, new JsonObject { ["required"] = Names("repository", "path") });
                break;
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static void RestrictEntry(JsonObject entry)
    {
        // These lifecycle actions are not permitted inside either a new entry or a replacement.
        var properties = entry["properties"]!.AsObject();
        foreach (var field in new[] { "supersedes", "same_as", "contradicts" }) properties.Remove(field);
    }
    private static JsonObject ActionBranch(string action, params string[] required)
    {
        var properties = new JsonObject { ["action"] = new JsonObject { ["enum"] = Names(action) } };
        foreach (var field in new[] { "replacement", "other", "other_expected_version", "draft" })
            if (!required.Contains(field)) properties[field] = false;
        return new() { ["properties"] = properties, ["required"] = Names(required) };
    }
    private static JsonObject KindBranch(string[] kinds, params string[] required)
    {
        // Match WorkService.RefuseForeignKindFields. Null optional fields disappear during
        // typed parsing; a non-null field of another kind is refused by the server.
        string[] fields = ["why", "alternatives", "context", "consequences", "made_autonomously", "why_failed", "what", "applies_when", "source"];
        string[] allowed = kinds[0] switch
        {
            "decision" => ["why", "alternatives", "context", "consequences", "made_autonomously"],
            "attempt" => ["why_failed"],
            "lesson" => ["what", "applies_when", "source"],
            _ => []
        };
        var properties = new JsonObject { ["kind"] = new JsonObject { ["enum"] = Names(kinds) } };
        foreach (var field in fields.Except(allowed)) properties[field] = new JsonObject { ["type"] = "null" };
        foreach (var field in required) properties[field] = new JsonObject { ["type"] = field == "source" ? "object" : "string" };
        return new() { ["properties"] = properties, ["required"] = Names(required) };
    }
    private static void Require(JsonObject schema, params string[] names)
    {
        var required = schema["required"] as JsonArray ?? [];
        foreach (var name in names) if (!required.Any(value => value!.GetValue<string>() == name)) required.Add(name);
        schema["required"] = required;
    }
    private static JsonArray Names(params string[] names) => new(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
}
