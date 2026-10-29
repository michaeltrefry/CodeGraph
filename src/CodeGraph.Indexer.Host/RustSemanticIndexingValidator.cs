using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
                await WriteFixtureAsync(fixtureRoot);
            }
            else if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"FAIL: Cargo manifest '{manifestPath}' does not exist.");
                return 66;
            }

            // Default production settings, except one crate per group so the fixture exercises
            // several groups and the cross-group link.
            var analyzer = new RustProjectAnalyzer(
                NullLogger<RustProjectAnalyzer>.Instance,
                Options.Create(new IndexingOptions { RustSemanticCrateGroupSize = ownsFixture ? 1 : 8 }));
            var context = new ExtractorContext
            {
                RepositoryName = ownsFixture
                    ? "RustSemanticValidation"
                    : Path.GetFileName(fixtureRoot),
                RootPath = fixtureRoot
            };
            var analysis = await analyzer.AnalyzeWorkspaceAsync(manifestPath, context);
            var failed = analysis.Groups.FirstOrDefault(group => group.Failure is not null);
            if (failed?.Failure is RustSemanticIndexingException groupFailure)
                throw groupFailure;
            if (failed?.Failure is { } otherFailure)
                throw otherFailure;

            var results = RustScipCrossGroupLinker.Link(
                analysis.Groups.SelectMany(group => group.Results ?? []).ToList());
            var nodes = results.SelectMany(result => result.Nodes).ToArray();
            var edges = results.SelectMany(result => result.Edges).ToArray();
            if (!ownsFixture)
                return ValidateRepositoryResult(fixtureRoot, nodes, edges);

            return ValidateFixtureResult(fixtureRoot, analysis, nodes, edges);
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

    private static async Task WriteFixtureAsync(string fixtureRoot)
    {
        async Task WriteAsync(string relativePath, string content)
        {
            var path = Path.Combine(fixtureRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        await WriteAsync("Cargo.toml",
            "[workspace]\nresolver = \"2\"\nmembers = [\"crates/alpha\", \"crates/beta\", \"crates/gamma\", \"crates/macros\"]\n");
        await WriteAsync("crates/alpha/Cargo.toml",
            "[package]\nname = \"alpha\"\nversion = \"0.1.0\"\nedition = \"2024\"\n\n" +
            "[dependencies]\nbeta = { path = \"../beta\" }\nmacros = { path = \"../macros\" }\n\n" +
            "[dev-dependencies]\ngamma = { path = \"../gamma\" }\n");
        // tests::uses_dev_dependency proves the lib sees cfg(test) and its dev-dependencies.
        await WriteAsync("crates/alpha/src/lib.rs",
            "macros::make_generated!();\n\npub fn caller() -> i32 { beta::target(41) }\n\n" +
            "#[cfg(test)]\nmod tests {\n    pub fn uses_dev_dependency() -> i32 { gamma::helper() }\n}\n");
        await WriteAsync("crates/beta/Cargo.toml",
            "[package]\nname = \"beta\"\nversion = \"0.1.0\"\nedition = \"2024\"\nbuild = \"build.rs\"\n");
        // Proves build scripts stay off: running it would leave a marker in the fixture.
        await WriteAsync("crates/beta/build.rs",
            "fn main() { std::fs::write(concat!(env!(\"CARGO_MANIFEST_DIR\"), \"/" + BuildScriptMarker +
            "\"), \"ran\").unwrap(); }\n");
        await WriteAsync("crates/beta/src/lib.rs", "pub fn target(value: i32) -> i32 { value + 1 }\n");
        await WriteAsync("crates/gamma/Cargo.toml",
            "[package]\nname = \"gamma\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
        await WriteAsync("crates/gamma/src/lib.rs", "pub fn helper() -> i32 { 1 }\n");
        await WriteAsync("crates/macros/Cargo.toml",
            "[package]\nname = \"macros\"\nversion = \"0.1.0\"\nedition = \"2024\"\n\n[lib]\nproc-macro = true\n");
        // Proves proc-macro expansion stays off: expanding it would define generated_by_macro.
        await WriteAsync("crates/macros/src/lib.rs",
            "use proc_macro::TokenStream;\n\n#[proc_macro]\npub fn make_generated(_input: TokenStream) -> TokenStream {\n" +
            "    \"pub fn generated_by_macro() {}\".parse().unwrap()\n}\n");
    }

    private const string BuildScriptMarker = "build-script-ran";

    internal static int ValidateFixtureResult(
        string fixtureRoot,
        RustWorkspaceAnalysis analysis,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges)
    {
        var groups = string.Join(" | ", analysis.Groups.Select(group => string.Join(",", group.Crates)));
        var caller = nodes.SingleOrDefault(node => IsExactLocalDefinition(node, "caller", "crates/alpha/src/lib.rs"));
        var target = nodes.SingleOrDefault(node => IsExactLocalDefinition(node, "target", "crates/beta/src/lib.rs"));
        var hasCallerDefinitionEdge = caller is not null && edges.Any(edge =>
            edge.Type == EdgeType.DEFINES &&
            edge.SourceQN == "RustSemanticValidation:crates/alpha/src/lib.rs" &&
            edge.TargetQN == caller.QualifiedName);
        var hasCrossGroupCall = caller is not null && target is not null && edges.Any(edge =>
            edge.Type == EdgeType.CALLS &&
            edge.SourceQN == caller.QualifiedName &&
            edge.TargetQN == target.QualifiedName);
        var testCaller = nodes.SingleOrDefault(node =>
            IsExactLocalDefinition(node, "uses_dev_dependency", "crates/alpha/src/lib.rs"));
        var helper = nodes.SingleOrDefault(node => IsExactLocalDefinition(node, "helper", "crates/gamma/src/lib.rs"));
        var hasDevDependencyCall = testCaller is not null && helper is not null && edges.Any(edge =>
            edge.Type == EdgeType.CALLS &&
            edge.SourceQN == testCaller.QualifiedName &&
            edge.TargetQN == helper.QualifiedName);
        var buildScriptRan = File.Exists(Path.Combine(fixtureRoot, "crates", "beta", BuildScriptMarker));
        var procMacroExpanded = nodes.Any(node => node.Name == "generated_by_macro");

        if (analysis.Groups.Count != 4 || caller is null || target is null || !hasCallerDefinitionEdge ||
            !hasCrossGroupCall || !hasDevDependencyCall || buildScriptRan || procMacroExpanded)
        {
            Console.Error.WriteLine(
                $"FAIL: grouped semantic import was incomplete (groups={groups}, caller={caller is not null}, " +
                $"target={target is not null}, callerDefinition={hasCallerDefinitionEdge}, " +
                $"crossGroupCall={hasCrossGroupCall}, devDependencyCall={hasDevDependencyCall}, " +
                $"buildScriptRan={buildScriptRan}, " +
                $"procMacroExpanded={procMacroExpanded}).");
            return 1;
        }

        Console.WriteLine(
            $"PASS: final image generated and imported Rust semantic definitions, references, cross-group CALLS " +
            $"and a cfg(test) CALLS into a dev-dependency " +
            $"for crate groups [{groups}] with build scripts and proc-macros off ({nodes.Count} nodes, {edges.Count} edges).");
        return 0;
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

    private static bool IsExactLocalDefinition(GraphNode node, string expectedName, string filePath) =>
        node.Name == expectedName &&
        node.FilePath == filePath &&
        node.Properties.GetValueOrDefault("source")?.ToString() == "scip" &&
        !node.Properties.ContainsKey("scip_external");
}
