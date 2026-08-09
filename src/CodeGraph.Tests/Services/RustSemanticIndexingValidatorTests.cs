using CodeGraph.Indexer.Host;
using CodeGraph.Models;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class RustSemanticIndexingValidatorTests : IDisposable
{
    private readonly string _parentRoot = Path.Combine(
        Path.GetTempPath(), $"codegraph-rust-validator-{Guid.NewGuid():N}");
    private readonly string _repositoryRoot;

    public RustSemanticIndexingValidatorTests()
    {
        _repositoryRoot = Path.Combine(_parentRoot, "repository");
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, "src"));
        File.WriteAllText(Path.Combine(_repositoryRoot, "src", "lib.rs"), "fn caller() {}\n");
    }

    [Fact]
    public void ValidateRepositoryResult_AcceptsFunctionOnlySemanticGraph()
    {
        var result = RustSemanticIndexingValidator.ValidateRepositoryResult(
            _repositoryRoot,
            [CreateLocalDefinition("src/lib.rs")],
            [
                new PendingEdge("file", "function", EdgeType.DEFINES_METHOD),
                new PendingEdge("function", "target", EdgeType.CALLS)
            ]);

        result.ShouldBe(0);
    }

    [Fact]
    public void ValidateRepositoryResult_RejectsInvalidLocalDefinitionPaths()
    {
        var outsidePath = Path.Combine(_parentRoot, "outside.rs");
        File.WriteAllText(outsidePath, "fn outside() {}\n");
        string[] invalidPaths =
        [
            "../outside.rs",
            Path.Combine(_repositoryRoot, "src", "lib.rs"),
            "",
            "src/missing.rs"
        ];

        foreach (var invalidPath in invalidPaths)
        {
            var result = RustSemanticIndexingValidator.ValidateRepositoryResult(
                _repositoryRoot,
                [CreateLocalDefinition(invalidPath)],
                [
                    new PendingEdge("file", "function", EdgeType.DEFINES_METHOD),
                    new PendingEdge("function", "target", EdgeType.CALLS)
                ]);

            result.ShouldBe(1, $"Path '{invalidPath}' should be rejected.");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentRoot, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static GraphNode CreateLocalDefinition(string filePath) => new()
    {
        Project = "RustValidation",
        Label = NodeLabel.Method,
        Name = "caller",
        QualifiedName = "RustValidation.caller",
        FilePath = filePath,
        Properties = new Dictionary<string, object> { ["source"] = "scip" }
    };
}
