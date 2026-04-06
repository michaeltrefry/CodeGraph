namespace TC.CodeGraphApi.Services.Analyzers;

public interface ILintRunner
{
    /// <summary>
    /// Runs ESLint against a repository and returns per-file error/warning counts.
    /// Returns an empty dictionary if ESLint is not available or not configured.
    /// </summary>
    Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(
        string repoPath, CancellationToken ct = default);
}

public record LintResult(int ErrorCount, int WarningCount);
