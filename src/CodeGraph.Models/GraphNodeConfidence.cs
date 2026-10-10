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
