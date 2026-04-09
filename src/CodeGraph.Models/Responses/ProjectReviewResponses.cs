namespace CodeGraph.Models.Responses;

public record StartProjectReviewResponse(long ReviewRunId, string Status);

public record ProjectReviewRunResponse(
    long Id,
    string Project,
    string ProjectName,
    string? ReviewedCommitSha,
    string Status,
    string ReviewMode,
    string PromptVersion,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error);

public record ProjectReviewFindingResponse(
    string Severity,
    string Category,
    string Title,
    string Explanation,
    string Evidence,
    string FilePath,
    int? LineStart,
    int? LineEnd,
    string SuggestedImprovement,
    string Confidence);

public record ProjectReviewResponse(
    ProjectReviewRunResponse Run,
    string Overview,
    IReadOnlyList<ProjectReviewFindingResponse> Findings,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps);

public record ProjectDiagnosticResponse(
    string Source,
    string DiagnosticId,
    string Severity,
    string Message,
    string? Category,
    string FilePath,
    int? LineStart,
    int? LineEnd,
    DateTime ComputedAt);

public record ProjectDiagnosticsResponse(
    string Project,
    string? DotnetProject,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<ProjectDiagnosticResponse> Diagnostics);
