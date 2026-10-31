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
        var savedBytes = DreamModelContext.CompactVerifiedSources(messages, "dream_verify", result, Delivered(messages));
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
        DreamModelContext.CompactVerifiedSources(messages, tool, Verified(journal), Delivered(messages)).ShouldBe(0);
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

    [Fact]
    public void Journaled_record_compacts_only_its_exact_previously_delivered_full_trail_evidence()
    {
        var cited = Guid.NewGuid().ToString(); var untouched = Guid.NewGuid().ToString();
        var messages = TrailMessages(cited, untouched);
        var delivered = Delivered(messages);
        var original = messages[1].Content!;
        var result = JsonSerializer.SerializeToElement(new { journal_id = 3, evidence = new[] { new { kind = "thread", reference = cited, summary = "Observed failure" } } });
        DreamModelContext.CompactRecordedTrail(messages, "dream_record", result, delivered).ShouldBeGreaterThan(7000);
        var data = JsonDocument.Parse(messages[1].Content!).RootElement.GetProperty("data");
        data.GetProperty("has_more").GetBoolean().ShouldBeTrue();
        data.GetProperty("next_cursor").GetString().ShouldBe("original-cursor");
        data.GetProperty("items")[0].TryGetProperty("data", out _).ShouldBeFalse();
        data.GetProperty("items")[0].GetProperty("reread").GetProperty("arguments").GetProperty("expected_hash").GetString().ShouldBe(new string('a', 64));
        data.GetProperty("items")[1].GetProperty("data").GetProperty("body").GetString().ShouldBe("UNCONSUMED_TRAIL");
        original.ShouldContain("FULL_CONSUMED_TRAIL");
        var second = JsonSerializer.SerializeToElement(new { journal_id = 4, evidence = new[] { new { kind = "thread", reference = untouched } } });
        DreamModelContext.CompactRecordedTrail(messages, "dream_record", second, delivered);
        JsonDocument.Parse(messages[1].Content!).RootElement.GetProperty("data").GetProperty("items")[1].TryGetProperty("data", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Unseen_or_refused_trail_results_are_never_compacted(bool delivered, bool journaled)
    {
        var cited = Guid.NewGuid().ToString(); var messages = TrailMessages(cited, Guid.NewGuid().ToString());
        var original = messages[1].Content;
        var result = JsonSerializer.SerializeToElement(new { journal_id = journaled ? (long?)4 : null, evidence = new[] { new { kind = "thread", reference = cited } } });
        DreamModelContext.CompactRecordedTrail(messages, "dream_record", result, delivered ? Delivered(messages) : 0).ShouldBe(0);
        messages[1].Content.ShouldBe(original);
    }

    [Fact]
    public void Reused_call_ids_do_not_make_a_new_same_batch_source_result_previously_delivered()
    {
        var messages = Messages(); var delivered = Delivered(messages);
        messages.Add(new("assistant", null, [new("used", "dream_read_source", JsonSerializer.SerializeToElement(new { }))]));
        messages.Add(messages[1] with { Content = messages[1].Content!.Replace("SOURCE_SENTINEL", "NEW_UNSEEN_SOURCE") });
        DreamModelContext.CompactVerifiedSources(messages, "dream_verify", Verified(9), delivered).ShouldBeGreaterThan(0);
        messages[^1].Content!.ShouldContain("NEW_UNSEEN_SOURCE");
    }

    [Fact]
    public void Replay_forecast_exposes_real_recent_charge_without_changing_the_allowance_or_overflowing()
    {
        var settings=new DreamSettings();
        var text=DreamModelContext.RuntimeBudget(settings,4,70000,58000,20000,4096);
        text.ShouldContain("Most recent generation charged 20000 input tokens");
        text.ShouldContain("roughly 64096 tokens");
        text.ShouldContain("planning estimate, not a new allowance");
        text.ShouldContain("remaining balance is 58000");
        text.ShouldContain("read.arguments");
        text.ShouldContain("same kind, id and expected_hash");
        DreamModelContext.RuntimeBudget(settings,1,0,128000).ShouldNotContain("Most recent generation");
        DreamModelContext.RuntimeBudget(settings,2,1,127999,long.MaxValue,4096).ShouldContain($"roughly {long.MaxValue} tokens");
        settings.ShouldBe(new DreamSettings());
    }

    private static List<DreamModelMessage> TrailMessages(string cited, string untouched) =>
    [
        new("assistant", null, [new("trail", "dream_work_trail", JsonSerializer.SerializeToElement(new { kind = "thread", id = cited, detail = "full", expected_hash = new string('a', 64) }))]),
        new("tool", DreamModelContext.ModelJson(JsonSerializer.SerializeToElement(new { untrusted_data = true, data = new
        {
            snapshot_at = "captured", total_count = 3, has_more = true, next_cursor = "original-cursor",
            items = new[] { new { kind = "thread", id = cited, data = new { body = "FULL_CONSUMED_TRAIL" + new string('x', 8000) } },
                new { kind = "thread", id = untouched, data = new { body = "UNCONSUMED_TRAIL" } } }
        } })), ToolCallId: "trail")
    ];

    private static int Delivered(List<DreamModelMessage> messages) => messages.Count;

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
