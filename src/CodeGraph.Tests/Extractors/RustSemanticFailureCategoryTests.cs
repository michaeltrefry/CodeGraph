using CodeGraph.Services.Extractors;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public sealed class RustSemanticFailureCategoryTests
{
    [Theory]
    // Markers apply only to the stderr tail of subprocess failures.
    [InlineData("rust_analyzer_scip_failed",
        "rust-analyzer scip exited with 1: error: failed to run custom build command for `alpha v0.1.0`",
        RustSemanticFailureCategory.BuildScript)]
    [InlineData("scip_print_failed",
        "scip print --json exited with 1: error: toolchain 'nightly-x86_64' is not installed",
        RustSemanticFailureCategory.Toolchain)]
    [InlineData("rust_analyzer_scip_failed",
        "rust-analyzer scip exited with 1: can't load standard library from sysroot",
        RustSemanticFailureCategory.Toolchain)]
    // Repository paths are not stderr: a path containing rustc/toolchain/build.rs is not evidence.
    [InlineData("rust_semantic_import_failed",
        "Rust semantic indexing failed for '/repos/rustc-perf/toolchain/build.rs/Cargo.toml'.",
        RustSemanticFailureCategory.Other)]
    // Only subprocess failure codes carry stderr, even if a path happens to look like a prefix.
    [InlineData("rust_semantic_import_failed",
        "Rust semantic indexing failed for '/repos/rustup-exited with 2: build script/Cargo.toml'.",
        RustSemanticFailureCategory.Other)]
    // rust-analyzer panic backtraces name ra_ap_rustc_* crates; that is not a toolchain failure.
    [InlineData("rust_analyzer_scip_failed",
        "rust-analyzer scip exited with 101: thread 'main' panicked at ra_ap_rustc_abi/src/layout.rs:42",
        RustSemanticFailureCategory.Other)]
    // The "exited with N:" prefix is required; text before it is ignored.
    [InlineData("rust_analyzer_scip_failed",
        "build script failed before rust-analyzer started",
        RustSemanticFailureCategory.Other)]
    public void Classify_UsesOnlySpecificStderrMarkers(string failureCode, string message, string expected)
    {
        RustSemanticFailureCategory.Classify(new RustSemanticIndexingException(failureCode, message))
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData("rust_semantic_group_timeout", RustSemanticFailureCategory.Timeout)]
    [InlineData("rust_semantic_workspace_timeout", RustSemanticFailureCategory.Timeout)]
    [InlineData("rust_semantic_memory_limit_exceeded", RustSemanticFailureCategory.ResourceExhausted)]
    [InlineData("rust_sysroot_unavailable", RustSemanticFailureCategory.Toolchain)]
    public void Classify_CrateGroupFailureCodes(string failureCode, string expected)
    {
        RustSemanticFailureCategory.Classify(new RustSemanticIndexingException(failureCode, "group failed"))
            .ShouldBe(expected);
    }

    [Fact]
    public void Classify_CargoMetadataStderr_UsesToolchainMarkers()
    {
        RustSemanticFailureCategory.Classify(new RustSemanticIndexingException(
                "cargo_metadata_failed",
                "cargo metadata exited with 1: error: toolchain '1.70.0-x86_64' is not installed"))
            .ShouldBe(RustSemanticFailureCategory.Toolchain);
    }

    [Fact]
    public void Classify_ImportFailureWrappingOutOfMemory_IsResourceExhausted()
    {
        var failure = new RustSemanticIndexingException(
            "rust_semantic_import_failed",
            "Rust semantic indexing failed for '/repos/alpha/Cargo.toml'.",
            new OutOfMemoryException());

        RustSemanticFailureCategory.Classify(failure).ShouldBe(RustSemanticFailureCategory.ResourceExhausted);
    }

    [Fact]
    public void Classify_ImportFailureWrappingTimeout_IsTimeout()
    {
        var failure = new RustSemanticIndexingException(
            "rust_semantic_import_failed",
            "Rust semantic indexing failed for '/repos/alpha/Cargo.toml'.",
            new TimeoutException());

        RustSemanticFailureCategory.Classify(failure).ShouldBe(RustSemanticFailureCategory.Timeout);
    }
}
