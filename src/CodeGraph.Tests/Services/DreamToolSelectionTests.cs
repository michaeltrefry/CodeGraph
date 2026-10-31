using System.Text.Json;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamToolSelectionTests
{
    [Fact]
    public void Confirmed_empty_domains_remove_their_read_schemas_and_keep_source_trail_and_writes_available()
    {
        var canonical = DreamingService.ModelTools;
        var selected = DreamToolSelection.Select(canonical, Digest());
        var excluded = new[] { "dream_read", "dream_lessons", "dream_similar", "dream_review_queue" };
        selected.Select(tool => tool.Name).Intersect(excluded).ShouldBeEmpty();
        selected.Select(tool => tool.Name).ShouldContain("dream_work_trail");
        selected.Select(tool => tool.Name).ShouldContain("dream_search_graph");
        selected.Select(tool => tool.Name).ShouldContain("dream_read_source");
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
        var selected = DreamToolSelection.Select(DreamingService.Tools, Digest(knowledge: 5, lessons: 3));
        selected.ShouldBe(DreamingService.Tools);
        selected.Zip(DreamingService.Tools).ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second));
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
}
