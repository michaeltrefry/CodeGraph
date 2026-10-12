namespace CodeGraph.Extractors.CSharp;

/// <summary>
/// Extraction version shared by <see cref="RoslynExtractor"/> and <see cref="SolutionAnalyzer"/>,
/// which both emit through <c>CodeGraphSyntaxWalker</c>. Bump it whenever C# extraction output
/// changes for unchanged source so existing repositories are re-extracted (see
/// <c>ExtractionVersionCatalog</c>).
/// </summary>
internal static class CSharpExtractionVersion
{
    /// <summary>
    /// 2: syntax-only extraction resolves <c>Type.M()</c>, <c>this.M()</c> and same-class
    /// <c>M()</c> calls (PR #84).
    /// </summary>
    public const string Current = "2";
}
