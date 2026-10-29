using System.Runtime.InteropServices;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Indexer.Host;

/// <summary>
/// Verifies in a built image that the Swift tree-sitter grammar compiled for its architecture loads
/// and extracts Swift: the grammar is a native library built in the image (TreeSitter.DotNet ships
/// none), so a broken or mis-targeted build only shows when the indexer loads it.
/// </summary>
internal static class SwiftGrammarValidator
{
    internal const string Command = "--validate-swift-grammar";

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.Ordinal);

    public static async Task<int> RunAsync()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var version = extractor.GetExtractionVersion(".swift");
        if (version.EndsWith(TreeSitterExtractor.GrammarUnavailableVersionSuffix, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"FAIL [swift_grammar_unavailable]: the Swift grammar cannot be loaded on {RuntimeInformation.ProcessArchitecture}.");
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "codegraph-swift-grammar-validation");
        var result = await extractor.ExtractAsync(
            Path.Combine(root, "Shape.swift"),
            "struct Shape {\n    func area() -> Double { 0 }\n}\n",
            new ExtractorContext { RepositoryName = "SwiftGrammarValidation", RootPath = root });
        if (!result.Succeeded || !result.Nodes.Any(node => node.Label == NodeLabel.Method && node.Name == "area"))
        {
            Console.Error.WriteLine(
                $"FAIL [swift_grammar_extraction_failed]: the Swift grammar loaded on {RuntimeInformation.ProcessArchitecture} but extracted no declarations.");
            return 1;
        }

        Console.WriteLine($"OK: Swift grammar loads and extracts on {RuntimeInformation.ProcessArchitecture} (extraction version {version}).");
        return 0;
    }
}
