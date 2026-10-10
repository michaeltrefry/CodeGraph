using System.Text.Json;
using CodeGraph.Services.Assistant;
using ModelContextProtocol.Protocol;

namespace CodeGraph.Tests.Services;

internal static class CallToolResultTestExtensions
{
    /// <summary>The first text block of a tool result.</summary>
    public static string Text(this CallToolResult result) => CommitAnswer.TextOf(result);

    /// <summary>The text after the leading <c>Commit:</c> line and blank line.</summary>
    public static string Body(this CallToolResult result)
    {
        var text = result.Text();
        var firstBreak = text.IndexOf("\n\n", StringComparison.Ordinal);
        return firstBreak < 0 ? text : text[(firstBreak + 2)..];
    }

    public static string? StructuredString(this CallToolResult result, string property)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured
            || !structured.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }
}
