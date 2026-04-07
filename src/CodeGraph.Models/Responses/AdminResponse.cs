namespace CodeGraph.Models.Responses;

public record ProcessReposResponse(
    List<string> Published,
    int Count);

public record DiscoverResponse(
    int Discovered,
    int Matched,
    int Published,
    int NewCount,
    int Skipped,
    List<string> Repos);

public record AnalysisBatchResponse(
    long Id,
    string Repo,
    string ProviderBatchId,
    string ProviderName,
    string ExecutionMode,
    bool IncludeAllSource,
    string Status,
    int RequestCount,
    int CompletedCount,
    DateTime SubmittedAt,
    DateTime? CompletedAt);

public record DatabaseHealthResponse(
    string Status,
    DateTime CapturedAt,
    int ConstraintCount,
    int ExpectedConstraintCount,
    List<string> MissingConstraints,
    int IndexCount,
    int ExpectedIndexCount,
    List<string> MissingIndexes,
    List<DatabaseIndexIssueResponse> OfflineIndexes,
    List<DatabaseDuplicateGroupResponse> DuplicateGroups);

public record DatabaseIndexIssueResponse(
    string Name,
    string Type,
    string State,
    string EntityType,
    List<string> LabelsOrTypes,
    List<string> Properties,
    string? FailureMessage);

public record DatabaseDuplicateGroupResponse(
    string Category,
    string Key,
    int Count,
    List<string> SampleValues);
