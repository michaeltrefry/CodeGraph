using System.Text.RegularExpressions;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work.Imports;

/// <summary>One native-memory file's text, as the importing agent read it (source files are never modified).</summary>
public sealed record MemoryFile(string Path, string Content);

/// <summary>A parsed candidate with the pre-check that seeds the agent's verification (design §10.3 step 2).</summary>
/// <param name="Candidate">The import_knowledge candidate; its verification is the pre-check's until the agent confirms or overrides it.</param>
/// <param name="Referenced">Repository paths the memory names.</param>
/// <param name="Missing">Those of them that no longer exist in the checkout.</param>
public sealed record MemoryCandidate(ImportCandidateInput Candidate, string Harness, string? MemoryType, IReadOnlyList<string> Referenced, IReadOnlyList<string> Missing);

/// <summary>
/// Turns Claude Code memory files (<c>~/.claude/projects/*/memory/*.md</c>, frontmatter name /
/// description / type) and Codex memory (<c>~/.codex/memories/MEMORY.md</c>, task groups with user
/// preferences, reusable knowledge and failures) into candidates for S5's import_knowledge. CodeGraph
/// makes no model call (P12): the pre-check only tests whether the repository paths a memory names
/// still exist; the importing agent verifies each candidate, and the owner accepts, edits or drops it.
/// </summary>
public static partial class NativeMemoryParser
{
    [GeneratedRegex(@"^---\s*\n(?<front>.*?)\n---\s*\n?(?<body>.*)$", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z_][\w-]*)\s*:\s*(?<value>.*?)\s*$")]
    private static partial Regex YamlLine();

    // Repository-relative paths: a/b/c, a/b.ext, or a file with a known extension.
    [GeneratedRegex(@"(?<![\w/.~-])(?<path>(?:[\w.-]+/)+[\w.-]+|[\w-]+\.(?:sln|csproj|cs|ts|json|md|sql|yml|yaml|sh|toml|rs|py))(?![\w/-])")]
    private static partial Regex PathToken();

    public static bool IsClaudeMemoryFile(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !System.IO.Path.GetFileName(path).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MemoryCandidate> ParseClaude(IEnumerable<MemoryFile> files, string? repositoryRoot)
    {
        var result = new List<MemoryCandidate>();
        var index = repositoryRoot is null ? null : new RepositoryIndex(repositoryRoot);
        foreach (var file in files.Where(file => IsClaudeMemoryFile(file.Path)).OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var text = file.Content.Replace("\r\n", "\n");
            var match = Frontmatter().Match(text);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var body = text;
            if (match.Success)
            {
                body = match.Groups["body"].Value;
                foreach (var line in match.Groups["front"].Value.Split('\n'))
                    if (YamlLine().Match(line) is { Success: true } pair && pair.Groups["value"].Value.Length > 0)
                        fields.TryAdd(pair.Groups["key"].Value, pair.Groups["value"].Value.Trim().Trim('"', '\''));
            }

            var type = fields.GetValueOrDefault("type")?.ToLowerInvariant();
            var summary = fields.GetValueOrDefault("description") ?? fields.GetValueOrDefault("name") ?? System.IO.Path.GetFileNameWithoutExtension(file.Path);
            var kind = type switch
            {
                "feedback" => KnowledgeKind.Lesson,
                _ => KnowledgeKind.Fact
            };
            result.Add(Candidate("claude", type, kind, summary, body.Trim(), fields.GetValueOrDefault("name"), file.Path, index,
                personal: type is "user" or "feedback"));
        }

        return result;
    }

    /// <summary>Codex task groups; with <paramref name="cwdFilter"/> only the groups whose applies_to or scope names it (a repository name).</summary>
    public static IReadOnlyList<MemoryCandidate> ParseCodex(MemoryFile file, string? repositoryRoot, string? cwdFilter)
    {
        var result = new List<MemoryCandidate>();
        var index = repositoryRoot is null ? null : new RepositoryIndex(repositoryRoot);
        var groups = Regex.Split(file.Content.Replace("\r\n", "\n"), @"(?m)^# Task Group:\s*").Skip(1);
        foreach (var group in groups)
        {
            var lines = group.Split('\n');
            var title = lines[0].Trim();
            var header = string.Join('\n', lines.TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal)));
            if (cwdFilter is not null && !header.Contains(cwdFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            string? section = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    section = line[3..].Trim().ToLowerInvariant();
                    continue;
                }

                if (!line.StartsWith("- ", StringComparison.Ordinal) || section is null)
                    continue;
                var (kind, personal) = section switch
                {
                    "user preferences" => (KnowledgeKind.Lesson, true),
                    "reusable knowledge" => (KnowledgeKind.Fact, false),
                    _ when section.StartsWith("failures", StringComparison.Ordinal) => (KnowledgeKind.Gotcha, false),
                    _ => ((KnowledgeKind?)null, false)
                };
                if (kind is null)
                    continue;
                var bullet = line[2..].Trim();
                result.Add(Candidate("codex", section, kind.Value, bullet, $"{bullet}\n\nFrom the Codex task group \"{title}\".", title,
                    $"{file.Path}#{title}", index, personal));
            }
        }

        return result;
    }

    private static MemoryCandidate Candidate(string harness, string? type, KnowledgeKind kind, string summary, string body, string? name, string sourceFile,
        RepositoryIndex? index, bool personal)
    {
        var tokens = PathToken().Matches(body + "\n" + summary).Select(match => match.Groups["path"].Value.TrimEnd('.', ','))
            .Where(path => !path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !path.Contains("://", StringComparison.Ordinal) && !path.StartsWith('~'))
            .Distinct(StringComparer.Ordinal).Take(KnowledgeLimits.MaxPaths).ToList();
        var referenced = new List<string>();
        var present = new List<string>();
        var missing = new List<string>();
        if (index is not null)
        {
            foreach (var token in tokens)
            {
                switch (index.Resolve(token))
                {
                    case (true, { } found):
                        referenced.Add(token);
                        present.Add(found);
                        break;
                    case (true, null):
                        referenced.Add(token);
                        missing.Add(token);
                        break;
                }
            }
        }
        else
        {
            referenced.AddRange(tokens);
        }

        var (verification, note) = personal
            ? (ImportVerification.Personal, "A preference about how the owner works: it belongs in harness memory, not shared project knowledge.")
            : index is null || referenced.Count == 0
                ? (ImportVerification.Unverifiable, "Names no repository path the pre-check can test; the importing agent verifies it against the code or infrastructure.")
                : missing.Count == 0
                    ? (ImportVerification.Verified, $"Every path it names exists: {string.Join(", ", present)}.")
                    : (ImportVerification.Stale, $"Names paths that no longer exist: {string.Join(", ", missing)}.");

        var entry = new KnowledgeEntryInput
        {
            Kind = kind,
            Summary = Cut(summary, KnowledgeLimits.MaxSummaryLength),
            Body = Cut(name is null || body.StartsWith(name, StringComparison.Ordinal) ? body : $"{name}\n\n{body}", KnowledgeLimits.MaxBodyLength),
            Confidence = KnowledgeConfidence.Medium,
            Paths = present.Count == 0 ? null : present,
            // A lesson needs what happened; the memory's text is it.
            What = kind == KnowledgeKind.Lesson ? Cut(body.Length == 0 ? summary : body, KnowledgeLimits.MaxFieldLength) : null
        };
        return new MemoryCandidate(
            new ImportCandidateInput { Entry = entry, SourceFile = Cut(sourceFile, KnowledgeLimits.MaxSourceFileLength), Verification = verification, VerificationNote = note },
            harness, type, referenced, missing);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// The files of a checkout, to tell a repository path a memory names from a phrase that only looks
    /// like one ("Python/Rust", "build/test"): a token with a slash is a path when it exists, when it has a
    /// file extension, or when it starts at a top-level entry of the repository; a bare file name counts
    /// only when some file has it (an unknown bare name may be an example, not a claim).
    /// </summary>
    private sealed class RepositoryIndex
    {
        private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".claude", ".angular", "dist" };

        private readonly string root;
        private readonly HashSet<string> topLevel;
        private readonly List<string> files = [];

        public RepositoryIndex(string root)
        {
            this.root = root;
            topLevel = Directory.EnumerateFileSystemEntries(root).Select(System.IO.Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var pending = new Stack<string>([root]);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory))
                    files.Add(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'));
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!Skipped.Contains(System.IO.Path.GetFileName(child)))
                        pending.Push(child);
            }
        }

        /// <returns>Whether the token is a repository path, and the path it resolves to (null: it no longer exists).</returns>
        public (bool IsPath, string? Found) Resolve(string token)
        {
            if (File.Exists(System.IO.Path.Combine(root, token)) || Directory.Exists(System.IO.Path.Combine(root, token)))
                return (true, token);
            var suffix = files.FirstOrDefault(file => file.EndsWith("/" + token, StringComparison.Ordinal));
            if (suffix is not null)
                return (true, suffix);
            if (!token.Contains('/'))
                return (false, null);
            var hasExtension = System.IO.Path.HasExtension(token) && !char.IsDigit(System.IO.Path.GetExtension(token).ElementAtOrDefault(1));
            return hasExtension || topLevel.Contains(token[..token.IndexOf('/')]) ? (true, null) : (false, null);
        }
    }
}
