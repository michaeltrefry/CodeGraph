namespace CodeGraph.Models.Messages;

/// <summary>
/// Published after a repository has been successfully indexed by the pipeline.
/// Triggers downstream work: cross-repo linking, vitals computation, analysis submission.
/// </summary>
public class RepositoryIndexingCompleted
{
    /// <summary>Short repo name, e.g. "TC.OrdersApi"</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute local path to the repository root.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>HTTPS clone URL, if known.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>HEAD commit SHA at time of indexing.</summary>
    public string? CommitSha { get; set; }

    /// <summary>Whether analysis should be submitted for this repo.</summary>
    public bool ShouldAnalyze { get; set; }

    /// <summary>Include all source in analysis prompts (not just convention-matched).</summary>
    public bool IncludeAllSource { get; set; }

    /// <summary>Whether vitals metrics should be computed.</summary>
    public bool ShouldComputeVitals { get; set; } = true;
}
