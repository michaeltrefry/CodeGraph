using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Reviews;

public sealed record ProjectReviewExecutionInput(
    string ReviewMode,
    IReadOnlyList<string>? SeedFiles = null,
    IReadOnlyList<string>? BlastRadiusFiles = null,
    IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>>? ChangedLineSpans = null,
    IReadOnlyList<string>? CandidateTests = null,
    string? UpdateSummary = null,
    ProjectReviewBaselineContext? BaselineContext = null);

public sealed record ProjectReviewLineSpan(int StartLine, int EndLine);

public sealed record ProjectReviewBaselineContext(
    string Overview,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<ProjectReviewFindingResponse> Findings);
