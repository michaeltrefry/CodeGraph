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
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine($"Usage: {Command} [cargo-manifest-path]");
            return 64;
        }

        var suppliedManifest = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
        var fixtureRoot = suppliedManifest is null
            ? Path.Combine(Path.GetTempPath(), $"codegraph-rust-validation-{Guid.NewGuid():N}")
            : Path.GetDirectoryName(suppliedManifest)!;
        var ownsFixture = suppliedManifest is null;
        try
        {
            var manifestPath = suppliedManifest ?? Path.Combine(fixtureRoot, "Cargo.toml");
            if (ownsFixture)
            {
                Directory.CreateDirectory(Path.Combine(fixtureRoot, "src"));
                await File.WriteAllTextAsync(
                    manifestPath,
                    "[package]\nname = \"codegraph_rust_validation\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
                await File.WriteAllTextAsync(
                    Path.Combine(fixtureRoot, "src", "lib.rs"),
                    "pub fn target(value: i32) -> i32 { value + 1 }\npub fn caller() -> i32 { target(41) }\n");
            }
            else if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"FAIL: Cargo manifest '{manifestPath}' does not exist.");
                return 66;
            }

            var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);
            var results = await analyzer.AnalyzeProjectAsync(
                manifestPath,
                new ExtractorContext
                {
                    ProjectName = ownsFixture
                        ? "RustSemanticValidation"
                        : Path.GetFileName(fixtureRoot),
                    RootPath = fixtureRoot
                });

            var nodes = results.SelectMany(result => result.Nodes).ToArray();
            var edges = results.SelectMany(result => result.Edges).ToArray();
            if (!ownsFixture)
                return ValidateRepositoryResult(fixtureRoot, nodes, edges);

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
            if (ownsFixture)
            {
                try { Directory.Delete(fixtureRoot, recursive: true); }
                catch { /* best-effort validation cleanup */ }
            }
        }
    }

    internal static int ValidateRepositoryResult(
        string repositoryRoot,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges)
    {
        var localDefinitions = nodes.Where(node =>
            node.Properties.GetValueOrDefault("source")?.ToString() == "scip" &&
            !node.Properties.ContainsKey("scip_external")).ToArray();
        var invalidSourceFiles = localDefinitions
            .Select(node => node.FilePath)
            .Distinct(StringComparer.Ordinal)
            .Where(path => !IsContainedSourceFile(repositoryRoot, path))
            .Select(path => string.IsNullOrWhiteSpace(path) ? "<empty>" : path)
            .Take(5)
            .ToArray();
        var definitionEdges = edges.Count(edge =>
            edge.Type is EdgeType.DEFINES or EdgeType.DEFINES_METHOD);
        var callEdges = edges.Count(edge => edge.Type == EdgeType.CALLS);

        if (localDefinitions.Length == 0 || definitionEdges == 0 ||
            callEdges == 0 || invalidSourceFiles.Length > 0)
        {
            Console.Error.WriteLine(
                $"FAIL: repository semantic import was incomplete " +
                $"(localDefinitions={localDefinitions.Length}, definitionEdges={definitionEdges}, " +
                $"callEdges={callEdges}, invalidSourceFiles={string.Join(',', invalidSourceFiles)}).");
            return 1;
        }

        Console.WriteLine(
            $"PASS: repository generated and imported Rust semantic data " +
            $"({nodes.Count} nodes, {edges.Count} edges, {localDefinitions.Length} local definitions, " +
            $"{definitionEdges} DEFINES, {callEdges} CALLS).");
        return 0;
    }

    private static bool IsContainedSourceFile(string repositoryRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            var canonicalPath = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return canonicalPath.StartsWith(
                       canonicalRoot + Path.DirectorySeparatorChar,
                       comparison) &&
                   File.Exists(canonicalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExactLocalDefinition(GraphNode node, string expectedName) =>
        node.Name == expectedName &&
        node.FilePath == "src/lib.rs" &&
        node.Properties.GetValueOrDefault("source")?.ToString() == "scip" &&
        !node.Properties.ContainsKey("scip_external");
}
