using System.Text.RegularExpressions;
namespace CodeGraph.Services.Memory;

public static partial class MemoryNormalizationService
{
    internal static string ToSnakeCase(string input)
    {
        var cleaned = SpecialCharsRegex().Replace(input, " ");
        cleaned = CamelCaseRegex().Replace(cleaned, "$1 $2");
        cleaned = WhitespaceRegex().Replace(cleaned.Trim(), "_");
        return cleaned.ToLowerInvariant();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_\s]")]
    private static partial Regex SpecialCharsRegex();

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
