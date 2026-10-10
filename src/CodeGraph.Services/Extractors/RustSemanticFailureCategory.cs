using System.Text.RegularExpressions;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Coarse, stable failure codes recorded when the rust-analyzer SCIP pass fails and a
/// Cargo workspace falls back to tree-sitter extraction.
/// </summary>
public static class RustSemanticFailureCategory
{
    public const string Timeout = "timeout";
    public const string ResourceExhausted = "resource_exhausted";
    public const string BuildScript = "build_script";
    public const string Toolchain = "toolchain";
    public const string Other = "other";

    // Only these failure codes carry a subprocess stderr tail in their message
    // ("<command> exited with N: <stderr>"); other messages may embed repository paths.
    private static readonly HashSet<string> StderrFailureCodes =
    [
        "rust_analyzer_scip_failed",
        "scip_print_failed",
        "cargo_metadata_failed"
    ];

    private static readonly Regex ExitedWithPrefix = new(
        @"exited with -?\d+:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Specific phrases rather than bare words: "rustc"/"toolchain"/"build.rs" also appear in
    // repository paths and rust-analyzer panic backtraces (ra_ap_rustc_* crates).
    private static readonly string[] BuildScriptMarkers =
    [
        "failed to run custom build command",
        "build script",
        "build-script-build"
    ];

    private static readonly string[] ToolchainMarkers =
    [
        "rustup",
        "toolchain '",
        "sysroot",
        "can't load standard library",
        "rust-src"
    ];

    public static string Classify(Exception failure)
    {
        if (failure is RustSemanticIndexingException semantic)
        {
            switch (semantic.FailureCode)
            {
                case "rust_semantic_command_timeout":
                case "rust_semantic_group_timeout":
                case "rust_semantic_workspace_timeout":
                    return Timeout;
                case "rust_semantic_resource_exhausted":
                case "rust_semantic_memory_limit_exceeded":
                    return ResourceExhausted;
                case "rust_semantic_tools_unavailable":
                case "rust_sysroot_unavailable":
                    return Toolchain;
            }

            // Wrapped failures (e.g. rust_semantic_import_failed) carry the real cause inner.
            if (semantic.InnerException is { } inner)
            {
                var innerCategory = ClassifyByType(inner);
                if (innerCategory != Other)
                    return innerCategory;
            }

            // rust-analyzer reports workspace-loading failures (build scripts, missing
            // toolchain components) only through its stderr tail in the message.
            if (StderrFailureCodes.Contains(semantic.FailureCode))
            {
                var stderr = ExtractStderr(semantic.Message);
                if (ContainsAny(stderr, BuildScriptMarkers))
                    return BuildScript;
                if (ContainsAny(stderr, ToolchainMarkers))
                    return Toolchain;
            }

            return Other;
        }

        return ClassifyByType(failure);
    }

    private static string ClassifyByType(Exception failure) => failure switch
    {
        TimeoutException or OperationCanceledException => Timeout,
        OutOfMemoryException or InsufficientExecutionStackException => ResourceExhausted,
        _ => Other
    };

    private static string ExtractStderr(string message)
    {
        var match = ExitedWithPrefix.Match(message);
        return match.Success ? message[(match.Index + match.Length)..] : string.Empty;
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
