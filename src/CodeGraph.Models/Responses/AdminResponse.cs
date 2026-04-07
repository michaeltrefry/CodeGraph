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
