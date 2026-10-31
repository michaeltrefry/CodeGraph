using System.Reflection;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class DreamKindSchemaTests
{
    [Fact]
    public void Lesson_views_are_the_four_supported_dream_views_and_cursor_continuations_stay_valid()
    {
        var schema = DreamModelContractTests.Schema("dream_lessons");
        schema.GetProperty("properties").GetProperty("view").GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(new[] { "recurrence", "unaddressed", "unmatched", "unlearned_reviews" });
        DreamModelContractTests.Required(schema).ShouldBeEmpty();
        schema.GetProperty("anyOf").EnumerateArray().SelectMany(DreamModelContractTests.Required).ShouldBe(new[] { "view", "cursor" });
    }

    [Theory]
    [InlineData("dream_record", "entry")]
    [InlineData("dream_propose", "replacement")]
    public void Kind_schema_field_legality_agrees_with_the_current_server_validator(string tool, string field)
    {
        var schema = DreamModelContractTests.Schema(tool).GetProperty("properties").GetProperty(field);
        var kinds = tool == "dream_record"
            ? new[] { KnowledgeKind.Fact, KnowledgeKind.Gotcha, KnowledgeKind.Constraint, KnowledgeKind.Attempt, KnowledgeKind.Lesson }
            : Enum.GetValues<KnowledgeKind>();
        var specificFields = new Dictionary<string, object>
        {
            ["why"] = "reason", ["alternatives"] = new[] { "alternative" }, ["context"] = "context", ["consequences"] = "consequence",
            ["made_autonomously"] = true, ["why_failed"] = "failure", ["what"] = "observed correction",
            ["applies_when"] = new { activity = new[] { "testing" } }, ["source"] = new { kind = "agent" }
        };
        foreach (var kind in kinds)
        foreach (var (name, value) in specificFields)
        {
            var arguments = new Dictionary<string, object> { ["kind"] = WorkJson.Name(kind), ["summary"] = "A complete scoped claim", [name] = value };
            if (kind == KnowledgeKind.Attempt) arguments["why_failed"] = "failure";
            if (kind == KnowledgeKind.Lesson && tool == "dream_record") arguments["source"] = new { kind = "agent" };
            var input = JsonSerializer.SerializeToElement(arguments);
            MatchesKindBranches(schema, input).ShouldBe(ServerAllows(kind, arguments.Keys), $"{tool}: {kind}.{name}");
        }
        // Scope/body/item fields are common to all kinds, and must stay present.
        foreach (var name in new[] { "body", "repositories", "paths", "nodes", "commit", "item" })
            schema.GetProperty("properties").TryGetProperty(name, out _).ShouldBeTrue(name);
    }

    [Fact]
    public void Required_attempt_failure_and_agent_lesson_source_are_branch_specific()
    {
        var record = DreamModelContractTests.Schema("dream_record").GetProperty("properties").GetProperty("entry");
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"fact","summary":"fact"}""")).ShouldBeTrue();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"fact","summary":"fact","what":"not a lesson"}""")).ShouldBeFalse();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"attempt","summary":"attempt"}""")).ShouldBeFalse();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"attempt","summary":"attempt","why_failed":null}""")).ShouldBeFalse();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"attempt","summary":"attempt","why_failed":"observed failure"}""")).ShouldBeTrue();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"lesson","summary":"lesson"}""")).ShouldBeFalse();
        MatchesKindBranches(record, JsonSerializer.Deserialize<JsonElement>("""{"kind":"lesson","summary":"lesson","source":{"kind":"agent"}}""")).ShouldBeTrue();
        var replacement = DreamModelContractTests.Schema("dream_propose").GetProperty("properties").GetProperty("replacement");
        // Replacement lessons do not acquire the direct-record-only source requirement.
        MatchesKindBranches(replacement, JsonSerializer.Deserialize<JsonElement>("""{"kind":"lesson","summary":"lesson"}""")).ShouldBeTrue();
        record.GetProperty("properties").GetProperty("source").GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ShouldBe(new[] { "agent" });
    }

    private static bool ServerAllows(KnowledgeKind kind, IEnumerable<string> names)
    {
        var validate = typeof(WorkService).GetMethod("RefuseForeignKindFields", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { validate.Invoke(null, [kind, names, "entry"]); return true; }
        catch (TargetInvocationException ex) when (ex.InnerException is WorkException { Code: WorkErrorCodes.InvalidArgument }) { return false; }
    }

    // Evaluate the added kind-discriminator/required/type fragment. Other schema constraints
    // retain their existing tests; field legality is compared to the actual server above.
    private static bool MatchesKindBranches(JsonElement schema, JsonElement input) =>
        input.EnumerateObject().All(property => schema.GetProperty("properties").TryGetProperty(property.Name, out _))
        && schema.GetProperty("anyOf").EnumerateArray().Any(branch =>
        {
            if (branch.GetProperty("required").EnumerateArray().Any(name => !input.TryGetProperty(name.GetString()!, out _))) return false;
            foreach (var rule in branch.GetProperty("properties").EnumerateObject())
            {
                if (!input.TryGetProperty(rule.Name, out var value)) continue;
                if (rule.Value.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(allowed => JsonElement.DeepEquals(allowed, value))) return false;
                if (!rule.Value.TryGetProperty("type", out var type)) continue;
                if (type.GetString() switch { "null" => value.ValueKind != JsonValueKind.Null, "string" => value.ValueKind != JsonValueKind.String,
                    "object" => value.ValueKind != JsonValueKind.Object, _ => false }) return false;
            }
            return true;
        });
}
