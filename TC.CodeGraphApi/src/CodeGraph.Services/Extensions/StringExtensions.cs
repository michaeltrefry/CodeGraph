namespace CodeGraph.Services.Extensions;

public static class StringExtensions
{
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
}
