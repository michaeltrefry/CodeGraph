namespace CodeGraph.Models.Responses;

public record StartRepositoryReviewResponse(long ReviewRunId, string Status);

public record RepositoryReviewRunResponse(
    long Id,
    string Repo,
    string? ReviewedCommitSha,
    long? BaselineReviewRunId,
    string? BaselineCommitSha,
    string Status,
    string ReviewMode,
    string PromptVersion,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error);

public record RepositoryReviewFindingResponse(
    string Severity,
    string Category,
    string Title,
    string Explanation,
    string Evidence,
    string FilePath,
    int? LineStart,
    int? LineEnd,
    string SuggestedImprovement,
    string Confidence,
    string? ProjectName);

public record RepositoryReviewProjectSectionResponse(
    string ProjectName,
    string Overview,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<RepositoryReviewFindingResponse> Findings,
    bool ReusedFromBaseline);

public record RepositoryReviewResponse(
    RepositoryReviewRunResponse Run,
    string Overview,
    IReadOnlyList<RepositoryReviewFindingResponse> Findings,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<RepositoryReviewProjectSectionResponse> ProjectReviews);
