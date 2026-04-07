using System.Text.RegularExpressions;

namespace CodeGraph.Services.Extensions;

public static class StringExtensions
{
    private static readonly Regex ThinkTagRegex =
        new(@"<think\b[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the string is not null, empty, or whitespace.
    /// </summary>
    public static bool HasValue(this string? s) => !string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Strip markdown code fences from a string (e.g. ```json ... ```).
    /// </summary>
    public static string StripCodeFences(this string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```"))
            json = json[(json.IndexOf('\n') + 1)..];
        if (json.EndsWith("```"))
            json = json[..json.LastIndexOf("```", StringComparison.Ordinal)].TrimEnd();
        return json;
    }

    /// <summary>
    /// Normalize structured model output by stripping common wrapper content such as
    /// markdown fences, think-tag reasoning blocks, and leading/trailing prose.
    /// Returns the first JSON object/array when one can be identified, otherwise a
    /// best-effort cleaned string.
    /// </summary>
    public static string NormalizeJsonResponse(this string text)
    {
        var cleaned = ThinkTagRegex.Replace(text, "").Trim();
        cleaned = cleaned.StripCodeFences().Trim();

        if (TryExtractTopLevelJson(cleaned, out var json))
            return json;

        var firstJsonChar = cleaned.IndexOfAny(['{', '[']);
        return firstJsonChar >= 0
            ? cleaned[firstJsonChar..].Trim()
            : cleaned;
    }

    private static bool TryExtractTopLevelJson(string text, out string json)
    {
        json = string.Empty;

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
            return false;

        var opening = text[start];
        var closing = opening == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = false;

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == opening)
            {
                depth++;
                continue;
            }

            if (c == closing)
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)].Trim();
                    return true;
                }
            }
        }

        return false;
    }
}
