using CodeGraph.Services.Models;

namespace CodeGraph.Services.Analyzers;

public interface ICodeAnalyzer
{
    /// <summary>
    /// Orchestrates analysis of an entire repository by fanning out per-project
    /// analysis in parallel, then synthesizing a repo-level summary.
    /// </summary>
    Task<RepoAnalysis> AnalyzeRepositoryAsync(string projectName,
        string rootPath, string? modelOverride = null,
        Func<ProjectAnalysis, Task>? onProjectComplete = null,
        CancellationToken ct = default);

    /// <summary>
    /// Analyzes a single project within a repository. Reads source files scoped
    /// to the project directory and sends a focused prompt to Claude.
    /// </summary>
    Task<ProjectAnalysis> AnalyzeProjectAsync(string projectName,
        string projectPath, string repoContext, string? modelOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Incrementally re-analyzes based on a diff and commit message.
    /// Returns null if the change is trivial and the existing summary still holds.
    /// </summary>
    Task<AnalysisUpdate?> AnalyzeChangesAsync(string projectName,
        string rootPath, string diff, string commitMessage,
        string existingSummary, CancellationToken ct = default);
}
