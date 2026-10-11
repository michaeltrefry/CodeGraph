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
    /// Consecutive failed indexing runs of the same repository commit after which routine
    /// (SkipIfUpToDate) syncs stop retrying it until HEAD moves or a manual reindex
    /// (SkipIfUpToDate=false) runs. The failure is recorded on the repository's sync state.
    /// 0 disables the suspension.
    /// </summary>
    public int MaxConsecutiveIndexFailures { get; set; } = 3;

    /// <summary>
    /// Maximum wall-clock time allowed for one rust-analyzer SCIP generation command.
    /// Large workspaces may need to resolve and compile build scripts on a cold cache.
    /// </summary>
    public int RustSemanticCommandTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Worker threads used while rust-analyzer primes semantic caches. Keep this aligned
    /// with the indexer container CPU allocation so a large workspace cannot create a
    /// host-sized worker pool inside a CPU-constrained container.
    /// </summary>
    public int RustSemanticMaxThreads { get; set; } = 2;

    /// <summary>
    /// Maximum stderr tail retained for Rust semantic command diagnostics.
    /// </summary>
    public int RustSemanticStderrTailCharacters { get; set; } = 4096;

    /// <summary>
    /// Cargo workspace members analyzed together in one rust-analyzer SCIP pass. Members are
    /// ordered by repository-relative manifest directory (ordinal), then package name, and
    /// chunked in that order, so the same workspace always yields the same groups. A failed
    /// group falls back to tree-sitter on its own; the other groups stay semantic.
    /// </summary>
    public int RustSemanticCrateGroupSize { get; set; } = 8;

    /// <summary>
    /// Wall-clock budget for one crate group: optional <c>cargo check</c> (build scripts),
    /// <c>rust-analyzer scip</c> and <c>scip print</c> together. Each of those commands is
    /// additionally bounded by <see cref="RustSemanticCommandTimeoutSeconds"/>.
    /// </summary>
    public int RustSemanticGroupTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Wall-clock budget for all crate groups of one Cargo workspace, which run one after another.
    /// Once it is spent the running group is stopped and every group not yet finished falls back
    /// to tree-sitter with failure code <c>rust_semantic_workspace_timeout</c>; the indexing run
    /// itself continues. Worst case per workspace is therefore this budget plus the up-front
    /// <c>cargo metadata</c> and sysroot lookups (each bounded by
    /// <see cref="RustSemanticCommandTimeoutSeconds"/>), instead of
    /// group count x <see cref="RustSemanticGroupTimeoutSeconds"/>.
    /// </summary>
    public int RustSemanticWorkspaceTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Resident-memory cap in MiB for the process tree of each crate-group command. On Linux the
    /// analyzer samples the summed RSS of the command's process group from /proc and kills the
    /// group once it exceeds the cap; other platforms do not enforce it. 0 disables the cap.
    /// </summary>
    public int RustSemanticGroupMemoryLimitMb { get; set; } = 8192;

    /// <summary>
    /// Run Cargo build scripts (<c>cargo check</c>) so rust-analyzer sees their generated code,
    /// cfgs and env. Off by default: build scripts are repository-controlled code.
    /// </summary>
    public bool RustSemanticBuildScripts { get; set; }

    /// <summary>
    /// Expand proc-macros with the toolchain's proc-macro server. Requires
    /// <see cref="RustSemanticBuildScripts"/>: proc-macro dylibs only exist after
    /// <c>cargo check</c> compiles them (which runs their build scripts).
    /// </summary>
    public bool RustSemanticProcMacros { get; set; }
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
