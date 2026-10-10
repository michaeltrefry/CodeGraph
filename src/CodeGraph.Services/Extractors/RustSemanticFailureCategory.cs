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

    private static readonly string[] BuildScriptMarkers =
    [
        "build script",
        "custom build command",
        "build.rs",
        "build-script-build"
    ];

    private static readonly string[] ToolchainMarkers =
    [
        "toolchain",
        "rustup",
        "sysroot",
        "rust-src",
        "rustc"
    ];

    public static string Classify(Exception failure)
    {
        if (failure is RustSemanticIndexingException semantic)
        {
            switch (semantic.FailureCode)
            {
                case "rust_semantic_command_timeout":
                    return Timeout;
                case "rust_semantic_resource_exhausted":
                    return ResourceExhausted;
                case "rust_semantic_tools_unavailable":
                    return Toolchain;
            }

            // rust-analyzer reports workspace-loading failures (build scripts, missing
            // toolchain components) only through its stderr tail in the message.
            if (ContainsAny(semantic.Message, BuildScriptMarkers))
                return BuildScript;
            if (ContainsAny(semantic.Message, ToolchainMarkers))
                return Toolchain;
            return Other;
        }

        return failure switch
        {
            TimeoutException or OperationCanceledException => Timeout,
            OutOfMemoryException or InsufficientExecutionStackException => ResourceExhausted,
            _ => Other
        };
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
