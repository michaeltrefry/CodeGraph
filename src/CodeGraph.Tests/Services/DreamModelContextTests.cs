using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamModelContextTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Compact_catalog_removes_repeated_documentation_without_removing_a_capability()
    {
        var original = JsonSerializer.Serialize(DreamingService.Tools);
        var compact = JsonSerializer.Serialize(DreamingService.ModelTools);
        output.WriteLine($"Canonical catalog chars: {original.Length}; model catalog chars: {compact.Length}");
        DreamingService.ModelTools.Select(t => t.Name).ShouldBe(DreamingService.Tools.Select(t => t.Name));
        compact.Length.ShouldBeLessThan(original.Length * 3 / 4);
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","description":"Long help","additionalProperties":false,
             "properties":{"description":{"type":"string","description":"Help","minLength":2},"examples":{"type":"array","items":{"type":"integer","minimum":1}}},
             "required":["description"],"$defs":{"title":{"type":"string","maxLength":7}},
             "anyOf":[{"required":["examples"],"not":{"properties":{"description":{"const":"bad"}}}}]}
            """);
        var actual = DreamModelContext.CompactSchema(schema);
        actual.TryGetProperty("description", out _).ShouldBeFalse();
        actual.GetProperty("properties").GetProperty("description").GetProperty("minLength").GetInt32().ShouldBe(2);
        actual.GetProperty("properties").GetProperty("examples").GetProperty("items").GetProperty("minimum").GetInt32().ShouldBe(1);
        actual.GetProperty("$defs").GetProperty("title").GetProperty("maxLength").GetInt32().ShouldBe(7);
        actual.GetProperty("anyOf")[0].GetProperty("not").GetRawText().ShouldContain("bad");
        actual.GetProperty("required")[0].GetString().ShouldBe("description");
        actual.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("description").GetString().ShouldBe("Long help");
    }

    [Fact]
    public void Model_json_preserves_untrusted_source_exactly_without_html_escape_amplification()
    {
        var value = JsonSerializer.SerializeToElement(new { untrusted_data = new { source = "if (a < 2) return \"café 界\"; // & <script>\n" } });
        var compact = DreamModelContext.ModelJson(value);
        compact.ShouldNotContain("\\u0022"); compact.ShouldNotContain("\\u003C");
        Encoding.UTF8.GetByteCount(compact).ShouldBeLessThan(Encoding.UTF8.GetByteCount(value.GetRawText()));
        JsonDocument.Parse(compact).RootElement.GetProperty("untrusted_data").GetProperty("source").GetString()
            .ShouldBe(value.GetProperty("untrusted_data").GetProperty("source").GetString());
    }

    [Fact]
    public void Only_source_consumed_by_a_successful_journaled_verification_is_compacted()
    {
        var messages = Messages();
        var full = messages[1].Content;
        var result = Verified(journal: 9);
        var savedBytes = DreamModelContext.CompactVerifiedSources(messages, "dream_verify", result);
        savedBytes.ShouldBeGreaterThan(50000);
        messages[1].Content!.ShouldNotContain("SOURCE_SENTINEL");
        using var receipt = JsonDocument.Parse(messages[1].Content!);
        var source = receipt.RootElement.GetProperty("untrusted_data");
        source.GetProperty("content_hash").GetString().ShouldBe("actual-hash");
        source.GetProperty("compacted_after").GetProperty("journal_id").GetInt64().ShouldBe(9);
        source.GetProperty("full_result_in_run_report").GetBoolean().ShouldBeTrue();
        source.GetProperty("reread").GetProperty("arguments").GetProperty("path").GetString().ShouldBe("Used.cs");
        messages[2].Content!.ShouldContain("UNCONSUMED_SOURCE");
        // Evidence received BEFORE the write stays full; only the subsequent replay changes.
        full!.ShouldContain("SOURCE_SENTINEL");
        var oldReplay = Messages().Sum(m => m.Content?.Length ?? 0);
        messages.Sum(m => m.Content?.Length ?? 0).ShouldBeLessThan(oldReplay / 3);
        output.WriteLine($"Source replay chars before: {oldReplay}; after durable verification: {messages.Sum(m => m.Content?.Length ?? 0)}; removed bytes: {savedBytes}");
    }

    [Theory]
    [InlineData("dream_verify", null)]
    [InlineData("dream_verify", 0L)]
    [InlineData("dream_propose", 9L)]
    [InlineData("dream_record", 9L)]
    public void Refusals_noops_and_other_writes_do_not_evict_source(string tool, long? journal)
    {
        var messages = Messages(); var original = messages.Select(m => m.Content).ToArray();
        DreamModelContext.CompactVerifiedSources(messages, tool, Verified(journal)).ShouldBe(0);
        messages.Select(m => m.Content).ShouldBe(original);
    }

    [Fact]
    public void Runtime_feedback_exposes_the_real_shared_budget_and_last_turn_without_changing_settings()
    {
        var settings = new DreamSettings();
        var text = DreamModelContext.RuntimeBudget(settings, 12, 82000, 46000);
        text.ShouldContain("128000"); text.ShouldContain("82000"); text.ShouldContain("46000");
        text.ShouldContain("Turn 12 of 12"); text.ShouldContain("last allowed turn");
        text.ShouldContain("ALL replayed input"); text.ShouldContain("uninspected coverage");
        settings.ShouldBe(new DreamSettings());
    }

    private static JsonElement Verified(long? journal) => JsonSerializer.SerializeToElement(new
    {
        journal_id = journal,
        data = new { entry = new { id = Guid.NewGuid(), verified_files = new[] { new { repository = "repo", path = "Used.cs" } } } }
    });

    private static List<DreamModelMessage> Messages() =>
    [
        new("assistant", null, [new("used", "dream_read_source", JsonSerializer.SerializeToElement(new { })), new("unused", "dream_read_source", JsonSerializer.SerializeToElement(new { }))]),
        new("tool", DreamModelContext.ModelJson(JsonSerializer.SerializeToElement(new { untrusted_data = new { repository = "repo", path = "Used.cs", content_hash = "actual-hash", indexed_hash = "actual-hash", source = "SOURCE_SENTINEL" + new string('x', 60000) } })), ToolCallId: "used"),
        new("tool", DreamModelContext.ModelJson(JsonSerializer.SerializeToElement(new { untrusted_data = new { repository = "repo", path = "Other.cs", source = "UNCONSUMED_SOURCE" } })), ToolCallId: "unused")
    ];
}
