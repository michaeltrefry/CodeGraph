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
    /// semantic pass, so their Rust nodes are always tree-sitter fallback nodes.
    /// </summary>
    public const string OverlayFallbackReason = "overlay";

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
