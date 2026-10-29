using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The lexical similarity of lessons (sc-27873, E5): deterministic, no model and no network; it
/// lists candidates and flags opposite polarity, and never decides a merge.
/// </summary>
public sealed class LessonSimilarityTests
{
    [Fact]
    public void Always_and_never_read_alike_but_are_flagged_as_opposite()
    {
        LessonSimilarity.Score("Always rebase onto main before merging", "Never rebase onto main before merging").ShouldBe(1.0);
        LessonSimilarity.Negates("Always rebase onto main before merging").ShouldBeFalse();
        LessonSimilarity.Negates("Don't rebase onto main before merging").ShouldBeTrue();
        LessonSimilarity.Negates("Never rebase onto main before merging").ShouldBeTrue();
    }

    [Fact]
    public void Paraphrases_score_above_the_candidate_threshold_and_unrelated_lessons_below()
    {
        LessonSimilarity.Score("Run the tests before pushing", "Run the test suite before pushing").ShouldBeGreaterThanOrEqualTo(LessonSimilarity.CandidateThreshold);
        LessonSimilarity.Score("Run the tests before pushing", "Read workflow states before moving stories").ShouldBeLessThan(LessonSimilarity.CandidateThreshold);
        LessonSimilarity.Score("", "anything").ShouldBe(0);
    }

    [Fact]
    public void The_score_is_symmetric_and_repeatable()
    {
        const string a = "When slicing a plan, don't restate epic requirements in stories";
        const string b = "Do not restate the epic's requirements in each story when slicing";
        LessonSimilarity.Score(a, b).ShouldBe(LessonSimilarity.Score(b, a));
        LessonSimilarity.Score(a, b).ShouldBe(LessonSimilarity.Score(a, b));
    }

    [Fact]
    public async Task Lesson_tools_are_strict_and_list_their_valid_arguments()
    {
        var service = new WorkService();
        var refusal = await Should.ThrowAsync<WorkException>(() => service.ExecuteAsync(WorkTools.Promote,
            System.Text.Json.JsonDocument.Parse("""{"id":"x","expected_version":1,"action":"promote_now"}""").RootElement, new WorkActor("alice")));
        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var unknown = await Should.ThrowAsync<WorkException>(() => service.ExecuteAsync(WorkTools.Challenge,
            System.Text.Json.JsonDocument.Parse("""{"open":{"id":"x","reason":"r","evidence":[]},"author":"claude"}""").RootElement, new WorkActor("alice")));
        unknown.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        unknown.Message.ShouldContain("author");
    }
}
