namespace CodeGraph.Services.Configuration;

public class IndexingOptions
{
    public int MaxParallelFiles { get; set; } = 8;

    /// <summary>
    /// Maximum number of repositories processed concurrently.
    /// Limits resource consumption (Neo4j connections, disk I/O, Roslyn workspaces).
    /// </summary>
    public int MaxParallelRepos { get; set; } = 4;

    /// <summary>
    /// Recompute fleet-wide communities after each repository finishes indexing.
    /// Disable during bulk ingestion to avoid rerunning Louvain on every repo.
    /// </summary>
    public bool DetectCommunitiesAfterIndexing { get; set; } = false;

    public int MaxFileSizeKb { get; set; } = 512;

    /// <summary>
    /// Maximum wall-clock time allowed for one rust-analyzer SCIP generation command.
    /// Large workspaces may need to resolve and compile build scripts on a cold cache.
    /// </summary>
    public int RustSemanticCommandTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum stderr tail retained for Rust semantic command diagnostics.
    /// </summary>
    public int RustSemanticStderrTailCharacters { get; set; } = 4096;
    public string[] SkipPatterns { get; set; } =
    [
        "**/bin/**", "**/obj/**", "**/node_modules/**",
        "**/build/**", "**/managed_components/**", "**/.cache/**",
        "**/wwwroot/lib/**", "**/*.min.js", "**/.git/**",
        "**/packages/**", "**/TestResults/**"
    ];

    public string[] FoundationalRepos { get; set; } = [];

    /// <summary>
    /// Comma-separated canonical repository identities that may execute repository-controlled
    /// .NET tooling (restore and MSBuild solution analysis). Identities are provider-qualified:
    /// github:https://host/owner/repo, gitlab:https://host/group/repo, or folder:relative/path.
    /// Folder identity paths are case-sensitive on non-Windows hosts.
    /// Empty by default.
    /// </summary>
    public string TrustedDotnetRepositories { get; set; } = "";

    public bool IsDotnetToolingTrusted(string? canonicalIdentity)
    {
        if (!TrySplitIdentity(canonicalIdentity, out var provider, out var value))
            return false;

        var valueComparison = provider is "folder" or "folder-path"
            ? (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            : StringComparison.Ordinal;

        return TrustedDotnetRepositories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(trustedIdentity =>
                TrySplitIdentity(trustedIdentity, out var trustedProvider, out var trustedValue)
                && string.Equals(provider, trustedProvider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value, trustedValue, valueComparison));
    }

    private static bool TrySplitIdentity(
        string? canonicalIdentity,
        out string provider,
        out string value)
    {
        var normalized = canonicalIdentity?.Trim().TrimEnd('/');
        var separator = normalized?.IndexOf(':') ?? -1;
        if (separator <= 0 || separator == normalized!.Length - 1)
        {
            provider = "";
            value = "";
            return false;
        }

        provider = normalized[..separator].ToLowerInvariant();
        value = normalized[(separator + 1)..];
        return true;
    }

    public string? ConventionsPath { get; set; }
}
