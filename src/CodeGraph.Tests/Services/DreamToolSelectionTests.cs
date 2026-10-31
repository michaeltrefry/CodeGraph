using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamToolSelectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Confirmed_empty_domains_remove_impossible_actions_and_keep_source_trail_and_record_available()
    {
        var canonical = DreamingService.ModelTools;
        var selected = DreamToolSelection.Select(canonical, Digest());
        var excluded = new[] { "dream_read", "dream_lessons", "dream_similar", "dream_review_queue", "dream_verify", "dream_propose",
            "dream_revise", "dream_promote", "dream_challenge", "dream_relate" };
        selected.Select(tool => tool.Name).Intersect(excluded).ShouldBeEmpty();
        selected.Select(tool => tool.Name).ShouldContain("dream_work_trail");
        selected.Select(tool => tool.Name).ShouldContain("dream_search_graph");
        selected.Select(tool => tool.Name).ShouldContain("dream_read_source");
        selected.Select(tool => tool.Name).ShouldContain("dream_record");
        foreach (var tool in canonical.Where(tool => !excluded.Contains(tool.Name))) selected.ShouldContain(tool);
        foreach (var name in excluded) canonical.Select(tool => tool.Name).ShouldContain(name);
        JsonSerializer.Serialize(selected).Length.ShouldBeLessThan(JsonSerializer.Serialize(canonical).Length);
    }

    [Fact]
    public void New_facts_enable_knowledge_reads_without_reopening_empty_lesson_views()
    {
        var selected = DreamToolSelection.Select(DreamingService.Tools, Digest(), newEntryCount: 1);
        selected.Select(tool => tool.Name).ShouldContain("dream_read");
        selected.Select(tool => tool.Name).ShouldNotContain("dream_lessons");
        selected.Select(tool => tool.Name).ShouldNotContain("dream_similar");
        selected.Select(tool => tool.Name).ShouldContain("dream_verify");
        selected.Select(tool => tool.Name).ShouldContain("dream_propose");
        foreach (var name in new[] { "dream_revise", "dream_promote", "dream_challenge", "dream_relate" })
            selected.Select(tool => tool.Name).ShouldNotContain(name);
        // Positive knowledge keeps review candidates reachable without pretending a zero
        // proposal count proves no autonomous decisions, proposed rules or contradictions.
        selected.Select(tool => tool.Name).ShouldContain("dream_review_queue");
    }

    [Fact]
    public void New_lessons_enable_lesson_reads_and_pairs_only_when_a_pair_can_exist()
    {
        var first = DreamToolSelection.Select(DreamingService.Tools, Digest(), newEntryCount: 1, newLessonCount: 1);
        first.Select(tool => tool.Name).ShouldContain("dream_lessons");
        first.Select(tool => tool.Name).ShouldNotContain("dream_similar");
        foreach (var name in new[] { "dream_revise", "dream_promote", "dream_challenge" })
            first.Select(tool => tool.Name).ShouldContain(name);
        first.Select(tool => tool.Name).ShouldNotContain("dream_relate");
        var second = DreamToolSelection.Select(DreamingService.Tools, Digest(), newEntryCount: 2, newLessonCount: 2);
        second.Select(tool => tool.Name).ShouldBe(DreamingService.Tools.Select(tool => tool.Name));
        var reviewOnly = DreamToolSelection.Select(DreamingService.Tools, Digest(reviews: 1));
        reviewOnly.Select(tool => tool.Name).ShouldContain("dream_lessons");
        reviewOnly.Select(tool => tool.Name).ShouldNotContain("dream_similar");
    }

    [Fact]
    public void Queue_candidates_and_populated_domains_retain_their_original_tool_definitions()
    {
        foreach (var digest in new[] { Digest(knowledge: 1), Digest(challenges: 1), Digest(proposals: 1) })
            DreamToolSelection.Select(DreamingService.Tools, digest).Select(tool => tool.Name).ShouldContain("dream_review_queue");
        var selected = DreamToolSelection.Select(DreamingService.Tools, Digest(knowledge: 5, lessons: 3, reviews: 1));
        selected.ShouldBe(DreamingService.Tools);
        selected.Zip(DreamingService.Tools).ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second));
    }

    [Fact]
    public void Relate_requires_two_lessons_even_when_many_other_entries_exist()
    {
        foreach (var lessons in new[] { 0, 1 })
            DreamToolSelection.Select(DreamingService.Tools, Digest(knowledge: 20, lessons: lessons))
                .Select(tool => tool.Name).ShouldNotContain("dream_relate");
        DreamToolSelection.Select(DreamingService.Tools, Digest(knowledge: 20, lessons: 2))
            .Select(tool => tool.Name).ShouldContain("dream_relate");
    }

    [Theory]
    [InlineData(0, 1, "unlearned_reviews")]
    [InlineData(1, 0, "recurrence,unaddressed,unmatched")]
    [InlineData(1, 1, "recurrence,unaddressed,unmatched,unlearned_reviews")]
    public void Lesson_views_match_real_domains_without_changing_other_fields_or_cursor_rules(long lessons, long reviews, string expected)
    {
        var canonical = DreamingService.ModelTools.Single(tool => tool.Name == "dream_lessons");
        var selected = DreamToolSelection.Select(DreamingService.ModelTools, Digest(knowledge: lessons, lessons: lessons, reviews: reviews))
            .Single(tool => tool.Name == "dream_lessons");
        Views(selected).ShouldBe(expected.Split(','));
        Views(canonical).ShouldBe(new[] { "recurrence", "unaddressed", "unmatched", "unlearned_reviews" });
        var before = JsonNode.Parse(canonical.Parameters.GetRawText())!;
        var after = JsonNode.Parse(selected.Parameters.GetRawText())!;
        before["properties"]!["view"]!.AsObject().Remove("enum");
        after["properties"]!["view"]!.AsObject().Remove("enum");
        JsonNode.DeepEquals(before, after).ShouldBeTrue();
        selected.Parameters.GetProperty("properties").TryGetProperty("cursor", out _).ShouldBeTrue();
        selected.Parameters.GetProperty("required").GetArrayLength().ShouldBe(0);
        selected.Parameters.GetProperty("anyOf").EnumerateArray().SelectMany(branch => branch.GetProperty("required").EnumerateArray())
            .Select(value => value.GetString()).ShouldContain("cursor");
    }

    [Fact]
    public void New_lessons_restore_legitimate_views_and_actions_without_mutating_an_earlier_projection()
    {
        var digest = Digest(reviews: 1);
        var before = DreamToolSelection.Select(DreamingService.ModelTools, digest);
        var after = DreamToolSelection.Select(DreamingService.ModelTools, digest, newEntryCount: 2, newLessonCount: 2);
        Views(before.Single(tool => tool.Name == "dream_lessons")).ShouldBe(new[] { "unlearned_reviews" });
        after.ShouldBe(DreamingService.ModelTools);
        after.Select(tool => tool.Name).ShouldContain("dream_relate");
    }

    [Fact]
    public void Catalog_size_metrics_cover_empty_review_only_facts_and_lessons()
    {
        var tools = DreamingService.ModelTools;
        var full = JsonSerializer.Serialize(tools).Length;
        foreach (var (name, digest) in new[] { ("empty", Digest()), ("review_only", Digest(reviews: 10)),
            ("facts_only", Digest(knowledge: 10)), ("one_lesson", Digest(knowledge: 10, lessons: 1)),
            ("two_lessons", Digest(knowledge: 10, lessons: 2)), ("all_domains", Digest(knowledge: 10, lessons: 2, reviews: 10)) })
        {
            var selected = DreamToolSelection.Select(tools, digest);
            var chars = JsonSerializer.Serialize(selected).Length;
            output.WriteLine($"{name}: canonical_model_catalog_chars={full}; selected_chars={chars}; saved_chars={full - chars}; tool_count={selected.Count}");
            chars.ShouldBeLessThanOrEqualTo(full);
            if (name != "all_domains") chars.ShouldBeLessThan(full);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"knowledge_count\":\"0\",\"lesson_count\":0,\"review_evidence_count\":0,\"open_challenge_count\":0,\"open_proposal_count\":0}")]
    [InlineData("{\"knowledge_count\":0,\"lesson_count\":-1,\"review_evidence_count\":0,\"open_challenge_count\":0,\"open_proposal_count\":0}")]
    [InlineData("{\"knowledge_count\":0,\"lesson_count\":1,\"review_evidence_count\":0,\"open_challenge_count\":0,\"open_proposal_count\":0}")]
    [InlineData("{\"knowledge_count\":0,\"lesson_count\":0,\"review_evidence_count\":0.5,\"open_challenge_count\":0,\"open_proposal_count\":0}")]
    public void Unknown_malformed_or_inconsistent_counts_never_hide_capabilities(string json) =>
        DreamToolSelection.Select(DreamingService.Tools, JsonSerializer.Deserialize<JsonElement>(json)).ShouldBeSameAs(DreamingService.Tools);

    [Fact]
    public void Unknown_record_results_and_counter_overflow_fall_back_to_the_full_catalog()
    {
        DreamToolSelection.Select(DreamingService.Tools, Digest(), unknownNewEntries: true).ShouldBeSameAs(DreamingService.Tools);
        DreamToolSelection.Select(DreamingService.Tools, Digest(), newEntryCount: 0, newLessonCount: 1).ShouldBeSameAs(DreamingService.Tools);
        DreamToolSelection.Select(DreamingService.Tools, Digest(knowledge: long.MaxValue), newEntryCount: 1).ShouldBeSameAs(DreamingService.Tools);
    }

    private static JsonElement Digest(long knowledge = 0, long lessons = 0, long reviews = 0, long challenges = 0, long proposals = 0) =>
        JsonSerializer.SerializeToElement(new { signals = new { knowledge_count = knowledge, lesson_count = lessons,
            review_evidence_count = reviews, open_challenge_count = challenges, open_proposal_count = proposals, work_trail_count = 0 } });

    private static string?[] Views(DreamToolDefinition tool) => tool.Parameters.GetProperty("properties").GetProperty("view")
        .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
}
