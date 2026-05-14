using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Data;

namespace CodeGraph.Services.WikiRag;

public partial class MarkdownWikiChunker : IMarkdownWikiChunker
{
    private const int TargetTokens = 512;
    private const int OverlapTokens = 64;

    public IReadOnlyList<ConventionChunk> Chunk(WikiPageEntity page, string sectionPath)
    {
        var markdown = StripFrontmatter(page.Content);
        var sections = SplitSections(markdown);
        var chunks = new List<ConventionChunk>();

        foreach (var section in sections)
        {
            if (IsPureCodeBlock(section.Content))
                continue;

            var breadcrumb = section.Breadcrumb.Count == 0
                ? sectionPath
                : $"{sectionPath} > {string.Join(" > ", section.Breadcrumb)}";

            foreach (var part in SplitLongSection(section.Content, TargetTokens, OverlapTokens))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                var index = chunks.Count;
                var embeddingText = $"{breadcrumb}\n\n{part.Trim()}";
                chunks.Add(ConventionChunk.FromPage(page, breadcrumb, index, part.Trim(), embeddingText));
            }
        }

        return chunks;
    }

    private static string StripFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
            return markdown;

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return markdown;

        var afterFence = normalized.IndexOf('\n', end + 4);
        return afterFence < 0 ? "" : normalized[(afterFence + 1)..];
    }

    private static IReadOnlyList<MarkdownSection> SplitSections(string markdown)
    {
        var sections = new List<MarkdownSection>();
        var breadcrumb = new SortedDictionary<int, string>();
        var current = new StringBuilder();
        var currentBreadcrumb = new List<string>();

        void Flush()
        {
            var text = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(new MarkdownSection(currentBreadcrumb, text));
            current.Clear();
        }

        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = HeadingRegex().Match(line);
            if (match.Success && match.Groups[1].Value.Length <= 3)
            {
                Flush();
                var level = match.Groups[1].Value.Length;
                var text = match.Groups[2].Value.Trim();
                breadcrumb[level] = $"{new string('#', level)} {text}";

                foreach (var key in breadcrumb.Keys.Where(k => k > level).ToList())
                    breadcrumb.Remove(key);

                currentBreadcrumb = breadcrumb.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
                continue;
            }

            current.AppendLine(line);
        }

        Flush();
        return sections;
    }

    private static IEnumerable<string> SplitLongSection(string text, int targetTokens, int overlapTokens)
    {
        var words = WordRegex().Matches(text).Select(m => m.Value).ToArray();
        if (words.Length <= targetTokens)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < words.Length)
        {
            var take = Math.Min(targetTokens, words.Length - start);
            yield return string.Join(' ', words.Skip(start).Take(take));
            if (start + take >= words.Length)
                yield break;
            start += Math.Max(1, targetTokens - overlapTokens);
        }
    }

    private static bool IsPureCodeBlock(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return false;

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence > 0 && lastFence >= trimmed.Length - 3;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    private sealed record MarkdownSection(IReadOnlyList<string> Breadcrumb, string Content);
}
