namespace TC.CodeGraphApi.Services.Analyzers;

public interface IVitalsAnalyzer
{
    /// <summary>
    /// Computes vitals metrics for all source files in a repository.
    /// Stores results in file_metrics and project_health_summaries tables.
    /// </summary>
    Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Sends vitals metrics to Claude for interpretation and stores the analysis
    /// in project_health_analyses. Call after ComputeMetricsAsync.
    /// </summary>
    Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default);
}
