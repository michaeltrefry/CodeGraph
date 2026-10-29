using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Models.Work;

/// <summary>
/// Paths and globs of knowledge scope: relative to a repository root with forward slashes, as
/// <c>file_hashes.rel_path</c> stores them. <c>**</c> matches across directories, <c>*</c> and
/// <c>?</c> within one. A path also covers everything below it as a directory.
/// </summary>
public static class KnowledgePaths
{
    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>The stored form of <paramref name="path"/>, or null when it is not a relative path.</summary>
    public static string? Normalize(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];
        value = value.Trim('/');
        if (value.Length == 0 || value.Any(char.IsControl) || value.Contains(':'))
            return null;
        var segments = value.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            return null;
        return value;
    }

    public static bool IsGlob(string path) => path.IndexOfAny(Wildcards) >= 0;

    /// <summary>The literal part before the first wildcard (the whole path when there is none).</summary>
    public static string Prefix(string path)
    {
        var index = path.IndexOfAny(Wildcards);
        return index < 0 ? path : path[..index];
    }

    /// <summary>
    /// Whether an entry scoped to <paramref name="entryPath"/> is about <paramref name="queryPath"/>:
    /// the same path, one below the other as directories, or one matching the other as a glob.
    /// </summary>
    public static bool Covers(string entryPath, string queryPath)
    {
        if (string.Equals(entryPath, queryPath, StringComparison.Ordinal))
            return true;

        var entryGlob = IsGlob(entryPath);
        var queryGlob = IsGlob(queryPath);
        if (entryGlob && Matches(entryPath, queryPath))
            return true;
        if (queryGlob && Matches(queryPath, entryPath))
            return true;
        if (!entryGlob && queryPath.StartsWith(entryPath + "/", StringComparison.Ordinal))
            return true;
        // A directory query covers the entries below it, globs included.
        if (!queryGlob && Prefix(entryPath).StartsWith(queryPath + "/", StringComparison.Ordinal))
            return true;
        return false;
    }

    public static bool Matches(string glob, string path) =>
        Regex.IsMatch(path, ToRegex(glob), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static string ToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
            {
                // "**/" matches zero or more directories; "**" alone anything.
                index++;
                if (index + 1 < glob.Length && glob[index + 1] == '/')
                {
                    index++;
                    pattern.Append("(?:.*/)?");
                }
                else
                {
                    pattern.Append(".*");
                }
            }
            else if (character == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (character == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(character.ToString()));
            }
        }

        // A glob also covers what lies below what it matches.
        return pattern.Append("(?:/.*)?$").ToString();
    }

    /// <summary>The dedup form of a summary: lower case, whitespace collapsed, trailing punctuation dropped.</summary>
    public static string NormalizeSummary(string summary)
    {
        var collapsed = Regex.Replace(summary.Trim().ToLowerInvariant(), @"\s+", " ");
        return collapsed.TrimEnd('.', '!', ';', ':', ',', ' ');
    }

    public static string HashSummary(string summary) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSummary(summary))));
}
