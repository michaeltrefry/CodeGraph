using System.Text.Json;

namespace CodeGraph.Models;

/// <summary>
/// Extraction-confidence marks carried in <see cref="GraphNode.Properties"/>.
/// </summary>
public static class GraphNodeConfidence
{
    public const string ConfidenceProperty = "confidence";
    public const string ExtractionSourceProperty = "extractionSource";
    public const string FallbackReasonProperty = "fallbackReason";
    public const string Low = "low";
    public const string TreeSitterFallbackSource = "tree-sitter-fallback";

    /// <summary>
    /// <see cref="FallbackReasonProperty"/> of a branch-overlay Rust node: overlays never run the
    /// Rust semantic pass, so their Rust nodes are always tree-sitter fallback nodes. Nodes of a
    /// language with no semantic tier at all (Swift) carry that language's reason instead,
    /// on the default branch and in overlays alike.
    /// </summary>
    public const string OverlayFallbackReason = "overlay";

    /// <summary>
    /// <see cref="FallbackReasonProperty"/> of every Swift node: Swift has no semantic extraction
    /// tier, so tree-sitter is the only (fallback) tier for it.
    /// </summary>
    public const string SwiftSemanticUnavailableReason = "swift_semantic_unavailable";

    /// <summary>
    /// Marks node properties as a tree-sitter fallback extraction: <c>confidence=low</c>,
    /// <c>extractionSource=tree-sitter-fallback</c> and the given <c>fallbackReason</c>.
    /// </summary>
    public static void MarkTreeSitterFallback(IDictionary<string, object> properties, string fallbackReason)
    {
        properties[ConfidenceProperty] = Low;
        properties[ExtractionSourceProperty] = TreeSitterFallbackSource;
        properties[FallbackReasonProperty] = fallbackReason;
    }

    public static bool IsLowConfidence(GraphNode node) =>
        node.Properties is not null &&
        node.Properties.TryGetValue(ConfidenceProperty, out var value) &&
        string.Equals(AsString(value), Low, StringComparison.OrdinalIgnoreCase);

    private static string? AsString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };
}
