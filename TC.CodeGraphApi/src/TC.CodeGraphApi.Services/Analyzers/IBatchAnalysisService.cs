namespace TC.CodeGraphApi.Services.Analyzers;

public interface IBatchAnalysisService
{
    /// <summary>
    /// Loads the full graph for the given repo, groups nodes by DotnetProject (.csproj),
    /// and submits one Anthropic batch request per project. Each request contains that
    /// project's nodes/edges + source code (budget applies per-project for better coverage).
    /// Persists AnalysisBatch + N AnalysisBatchRequest rows (one per project).
    /// Throws if no nodes exist (repo not indexed).
    /// When repoPath is provided, source code is included alongside the graph.
    /// Set includeAllSource to include code for all classes (for repos that don't
    /// follow the TC.* project convention). Source is capped by MaxSourceChars per project.
    /// </summary>
    Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null,
        bool includeAllSource = false, CancellationToken ct = default);

    /// <summary>
    /// Checks all submitted batches (optionally scoped to one repo) against the
    /// Anthropic Batches API. For completed batches, downloads per-project results
    /// and stores project summaries + per-node descriptions. Publishes
    /// ProjectAnalysisResultsProcessed to trigger synthesis asynchronously.
    /// Called by ProcessBatchResultsJob.
    /// </summary>
    Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default);

    /// <summary>
    /// Synthesize a repo-level summary from stored per-project analyses via a Claude API call.
    /// Called by ProjectAnalysisResultsProcessedConsumer.
    /// </summary>
    Task SynthesizeRepoSummaryAsync(string repoName, string batchId, CancellationToken ct);

    /// <summary>
    /// Write CODEGRAPH.md files (per-project and repo-level) to the local repo directory.
    /// Called by AnalysisSynthesisCompletedConsumer.
    /// </summary>
    Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct);
}
