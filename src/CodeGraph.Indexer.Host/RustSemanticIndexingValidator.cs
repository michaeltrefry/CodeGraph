using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Indexer.Host;

internal static class RustSemanticIndexingValidator
{
    internal const string Command = "--validate-rust-semantic-indexing";

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine($"Usage: {Command}");
            return 64;
        }

        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"codegraph-rust-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "src"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixtureRoot, "Cargo.toml"),
                "[package]\nname = \"codegraph_rust_validation\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
            await File.WriteAllTextAsync(
                Path.Combine(fixtureRoot, "src", "lib.rs"),
                "pub fn target(value: i32) -> i32 { value + 1 }\npub fn caller() -> i32 { target(41) }\n");

            var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);
            var results = await analyzer.AnalyzeProjectAsync(
                Path.Combine(fixtureRoot, "Cargo.toml"),
                new ExtractorContext
                {
                    ProjectName = "RustSemanticValidation",
                    RootPath = fixtureRoot
                });

            var nodes = results.SelectMany(result => result.Nodes).ToArray();
            var edges = results.SelectMany(result => result.Edges).ToArray();
            var caller = nodes.SingleOrDefault(node => IsExactLocalDefinition(node, "caller"));
            var target = nodes.SingleOrDefault(node => IsExactLocalDefinition(node, "target"));
            var fileQualifiedName = "RustSemanticValidation:src/lib.rs";
            var hasCallerDefinitionEdge = caller is not null && edges.Any(edge =>
                edge.Type == EdgeType.DEFINES &&
                edge.SourceQN == fileQualifiedName &&
                edge.TargetQN == caller.QualifiedName);
            var hasTargetDefinitionEdge = target is not null && edges.Any(edge =>
                edge.Type == EdgeType.DEFINES &&
                edge.SourceQN == fileQualifiedName &&
                edge.TargetQN == target.QualifiedName);
            var hasResolvedCall = caller is not null && target is not null && edges.Any(edge =>
                edge.Type == EdgeType.CALLS &&
                edge.SourceQN == caller.QualifiedName &&
                edge.TargetQN == target.QualifiedName);

            if (caller is null || target is null ||
                !hasCallerDefinitionEdge || !hasTargetDefinitionEdge || !hasResolvedCall)
            {
                Console.Error.WriteLine(
                    $"FAIL: semantic import was incomplete (caller={caller is not null}, " +
                    $"target={target is not null}, callerDefinition={hasCallerDefinitionEdge}, " +
                    $"targetDefinition={hasTargetDefinitionEdge}, resolvedCall={hasResolvedCall}).");
                return 1;
            }

            Console.WriteLine(
                $"PASS: final image generated and imported Rust semantic definitions, references, and CALLS " +
                $"({nodes.Length} nodes, {edges.Length} edges).");
            return 0;
        }
        catch (RustSemanticIndexingException ex)
        {
            Console.Error.WriteLine($"FAIL [{ex.FailureCode}]: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL [rust_semantic_validation_failed]: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(fixtureRoot, recursive: true); }
            catch { /* best-effort validation cleanup */ }
        }
    }

    private static bool IsExactLocalDefinition(GraphNode node, string expectedName) =>
        node.Name == expectedName &&
        node.FilePath == "src/lib.rs" &&
        node.Properties.GetValueOrDefault("source")?.ToString() == "scip" &&
        !node.Properties.ContainsKey("scip_external");
}
