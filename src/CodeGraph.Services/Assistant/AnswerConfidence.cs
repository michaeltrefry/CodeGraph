using CodeGraph.Models;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Shared wording for answers that include low-confidence (tree-sitter fallback) nodes, and for
/// commit-pinned answers that leave out default-branch edges the branch overlay could not map.
/// </summary>
public static class AnswerConfidence
{
    public const string NodeTag = " [low confidence]";

    public static string Tag(GraphNode node) => Tag(GraphNodeConfidence.IsLowConfidence(node));

    public static string Tag(bool lowConfidence) => lowConfidence ? NodeTag : "";

    public static int Count(IEnumerable<GraphNode?> nodes) => nodes
        .OfType<GraphNode>()
        .DistinctBy(node => (node.Repository, node.Id, node.QualifiedName))
        .Count(GraphNodeConfidence.IsLowConfidence);

    /// <summary>The answer-level line, or null when <paramref name="count"/> is zero.</summary>
    public static string? SummaryLine(int count) => count <= 0
        ? null
        : $"**Low confidence:** this answer includes {count} low-confidence node(s) indexed by the " +
          "tree-sitter fallback (semantic analysis failed, or was not run for a branch-overlay commit); " +
          "their relationships may be incomplete.";

    /// <summary>The answer-level line, or null when <paramref name="count"/> is zero.</summary>
    public static string? UnmappedLine(int count) => count <= 0
        ? null
        : $"**Unmapped callers:** {count} default-branch edge(s) from unchanged files into files changed " +
          "or deleted at this commit could not be mapped to the branch version of their target " +
          "(removed, renamed, or not matchable) and are left out of this answer.";
}
