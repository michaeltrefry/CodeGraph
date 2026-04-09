using System.Text;
using System.Text.Json;
using CodeGraph.Models;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Reviews;

internal static class RepositoryReviewSynthesisPromptBuilder
{
    public const string SystemPrompt =
        """
        You are synthesizing a repository-level code review from structured project review results.
        Return strict JSON only. Do not wrap the JSON in markdown.
        Do not invent evidence. Base every summary point on the supplied project review data.
        Keep the overview concise and scan-friendly for a repo details page.
        """;

    public static string Build(
        string repo,
        string mode,
        string? reviewedCommitSha,
        RepositoryReviewResponse? baselineReview,
        object executionPlan,
        IReadOnlyList<object> projectReviews,
        IReadOnlyList<object> topFindings)
    {
        var payload = new
        {
            repo,
            mode,
            reviewedCommitSha,
            executionPlan,
            baselineReview = baselineReview is null
                ? null
                : new
                {
                    baselineReview.Run.ReviewedCommitSha,
                    baselineReview.Overview,
                    baselineReview.Strengths,
                    baselineReview.ReviewedAreas,
                    baselineReview.SkippedAreas,
                    baselineReview.FollowUps,
                    Findings = baselineReview.Findings.Take(10).ToList()
                },
            projectReviews,
            topFindings
        };

        var sb = new StringBuilder();
        sb.AppendLine("Summarize this repository code review for the repo detail page.");
        sb.AppendLine();
        sb.AppendLine("Return JSON with this shape:");
        sb.AppendLine("""
            {
              "overview": "string",
              "strengths": ["string"],
              "reviewedAreas": ["string"],
              "skippedAreas": ["string"],
              "followUps": ["string"]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- The overview should describe the overall repo state across projects in 1-2 short paragraphs or 2 concise sentences.");
        sb.AppendLine("- Strengths should capture meaningful positives, not filler.");
        sb.AppendLine("- Reviewed areas should mention the main projects or subsystems reviewed.");
        sb.AppendLine("- Skipped areas should mention only real review limits.");
        sb.AppendLine("- Follow-ups should highlight the most useful next checks.");
        sb.AppendLine("- If this is an update review, reflect when unchanged project sections were reused from the baseline and avoid overstating certainty for them.");
        sb.AppendLine("- Do not repeat the same point across multiple lists.");
        sb.AppendLine();
        sb.AppendLine("Review input:");
        sb.AppendLine(JsonSerializer.Serialize(payload, CodeGraphJsonDefaults.CamelCase));
        return sb.ToString();
    }
}
