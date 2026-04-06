using Microsoft.CodeAnalysis.CSharp;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.CSharp;

public class RoslynExtractor : ICodeExtractor
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".cs" };

    /// <summary>
    /// Individual file extraction (fallback when no solution available).
    /// Without a compilation we get syntax-only analysis — still useful for
    /// structure, but no resolved types.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(string filePath,
        string content, ExtractorContext context, CancellationToken ct = default)
    {
        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        var root = await tree.GetRootAsync(ct);

        var walker = new CodeGraphSyntaxWalker(context, semanticModel: null);
        walker.Visit(root);
        return walker.GetResult();
    }
}
