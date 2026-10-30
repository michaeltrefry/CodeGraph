using System.Text.Json;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class DreamModelContractTests
{
    [Fact]
    public void Every_private_schema_is_self_contained_and_excludes_server_identity()
    {
        foreach (var tool in DreamingService.Tools)
        {
            var schema = tool.Parameters;
            var properties = schema.GetProperty("properties");
            schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
            foreach (var field in new[] { "project", "run_id", "session_id", "actor" }) properties.TryGetProperty(field, out _).ShouldBeFalse(tool.Name);
            CheckReferences(schema, schema);
        }
        static void CheckReferences(JsonElement node, JsonElement root)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("$ref", out var reference))
                {
                    var value = root;
                    var pointer = reference.GetString()!;
                    pointer.ShouldStartWith("#/");
                    foreach (var part in pointer[2..].Split('/'))
                        value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(part)] : value.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
                }
                foreach (var child in node.EnumerateObject()) CheckReferences(child.Value, root);
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) CheckReferences(child, root);
        }
    }

    [Fact]
    public void Every_write_exposes_complete_typed_evidence_and_action_specific_requirements()
    {
        foreach (var name in new[] { "dream_verify", "dream_revise", "dream_relate", "dream_promote", "dream_record", "dream_challenge" })
        {
            var schema = Schema(name);
            Required(schema).ShouldContain("evidence"); Required(schema).ShouldContain("prevalence");
            var evidence = schema.GetProperty("properties").GetProperty("evidence");
            evidence.GetProperty("minItems").GetInt32().ShouldBe(1);
            evidence.GetProperty("maxItems").GetInt32().ShouldBe(10);
            var citation = evidence.GetProperty("items");
            Required(citation).Order().ShouldBe(new[] { "kind", "reference", "summary" });
            citation.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
            var properties = citation.GetProperty("properties");
            foreach (var field in new[] { "kind", "reference", "summary" }) properties.GetProperty(field).GetProperty("type").GetString().ShouldBe("string");
            properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ShouldBe(
                new[] { "entry", "lesson_event", "item", "challenge", "message", "graph_node", "review", "attempt", "handoff", "thread" });
            properties.GetProperty("reference").GetProperty("description").GetString()!.ShouldContain("never a file path");
        }
        Required(Schema("dream_revise")).ShouldContain("applies_when");
        Schema("dream_relate").GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        var entry = Schema("dream_record").GetProperty("properties").GetProperty("entry");
        Required(Schema("dream_record")).ShouldContain("entry");
        Required(entry).ShouldContain("confidence");
        entry.GetProperty("properties").GetProperty("why_failed").GetProperty("type").GetString().ShouldBe("string");
        entry.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(x => x.GetString())
            .ShouldBe(new[] { "fact", "gotcha", "constraint", "attempt", "lesson" });
        var proposal = Schema("dream_propose");
        Required(proposal).Order().ShouldBe(new[] { "action", "evidence", "expected_version", "prevalence", "rationale", "target" });
        var proposalEvidence = proposal.GetProperty("properties").GetProperty("evidence").GetProperty("items");
        Required(proposalEvidence).Order().ShouldBe(new[] { "id", "kind" });
        proposalEvidence.GetProperty("properties").GetProperty("kind").GetProperty("enum").GetArrayLength().ShouldBe(5);
        var branches = proposal.GetProperty("anyOf").EnumerateArray().ToDictionary(
            branch => branch.GetProperty("properties").GetProperty("action").GetProperty("enum")[0].GetString()!, Required);
        branches.Count.ShouldBe(6);
        branches["supersede"].ShouldContain("replacement");
        branches["merge"].ShouldBe(new[] { "other", "other_expected_version" });
        foreach (var action in new[] { "skill_draft", "hook_draft", "test_draft" }) branches[action].ShouldBe(new[] { "draft" });
        Schema("dream_similar").GetProperty("properties").TryGetProperty("since", out _).ShouldBeFalse();
        Schema("dream_read_source").GetProperty("anyOf").GetArrayLength().ShouldBe(2);
        Schema("dream_read_source").GetProperty("properties").GetProperty("node_id").GetProperty("type").GetString().ShouldBe("integer");
        Required(Schema("dream_lessons")).ShouldBeEmpty();
        Schema("dream_lessons").GetProperty("anyOf").EnumerateArray().SelectMany(Required).ShouldBe(new[] { "view", "cursor" });
    }

    internal static JsonElement Schema(string name) => DreamingService.Tools.Single(tool => tool.Name == name).Parameters;
    internal static string[] Required(JsonElement schema) => schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray();
}
