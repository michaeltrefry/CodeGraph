using System.Text.RegularExpressions;
using CodeGraph.Models.Work;
using CodeGraph.Services.Analyzers;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Work;

/// <summary>
/// Overlap of the work areas of live sessions (design §5.6, R21): the claimed items' touches
/// (paths, directories, globs, symbols) overlap directly, or one touches something inside the
/// other's impact radius, taken from the same <see cref="IImpactAnalysisService"/> that backs
/// analyze_impact and scoped to the item's repository. Advisory: it only informs (E6).
/// The graph is consulted only for pairs that do not overlap directly, to a fixed depth, for a
/// bounded number of touches, within a wall-clock budget, and each lookup once per call; past the
/// budget or on any graph failure only direct overlaps are reported.
/// </summary>
public sealed class WorkOverlaps(IImpactAnalysisService? impact = null, ILogger? logger = null)
{
    /// <summary>The warnings for <paramref name="sessionId"/> against every other session's area, in item order.</summary>
    public async Task<IReadOnlyList<OverlapWarning>> WarningsForAsync(string sessionId, IReadOnlyList<WorkArea> areas, CancellationToken ct = default)
    {
        var mine = areas.Where(area => area.SessionId == sessionId).ToList();
        var others = areas.Where(area => area.SessionId != sessionId).ToList();
        if (mine.Count == 0 || others.Count == 0)
            return [];

        var warnings = new List<OverlapWarning>();
        var cache = new Dictionary<(string Repository, string Touch), Task<HashSet<(string Repository, string Text)>>>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(WorkOverlapLimits.ImpactBudgetMilliseconds);

        foreach (var yours in mine)
        {
            foreach (var theirs in others)
            {
                if (warnings.Count >= WorkOverlapLimits.MaxWarnings)
                    return warnings;
                if (FindDirect(yours, theirs) is { } direct)
                {
                    warnings.Add(Warn(OverlapKinds.Direct, yours, theirs, direct));
                    continue;
                }

                if (await FindThroughImpactAsync(yours, theirs, cache, budget.Token) is { } through)
                    warnings.Add(Warn(OverlapKinds.Impact, yours, theirs, through));
            }
        }

        return warnings;
    }

    private static OverlapWarning Warn(string kind, WorkArea yours, WorkArea theirs, string via)
    {
        var who = $"session {theirs.SessionId} ({theirs.Identity} {theirs.Harness}{(theirs.Model is null ? "" : " " + theirs.Model)}{(theirs.Host is null ? "" : " on " + theirs.Host)})";
        var how = kind == OverlapKinds.Direct ? "overlaps directly" : "overlaps through the impact radius";
        return new OverlapWarning(kind, yours.ItemKey, theirs.ItemKey, theirs.SessionId, theirs.Identity, theirs.Harness, via,
            $"Your {yours.ItemKey} {how} with {theirs.ItemKey}, claimed by {who}: {via}. Advisory only; message that session to coordinate.");
    }

    // ---- Direct overlap -----------------------------------------------------------------------------

    private static bool SameRepository(WorkArea a, WorkArea b) =>
        a.Repository is null || b.Repository is null || string.Equals(a.Repository, b.Repository, StringComparison.OrdinalIgnoreCase);

    private static string? FindDirect(WorkArea a, WorkArea b)
    {
        if (!SameRepository(a, b))
            return null;
        foreach (var x in a.Touches)
            foreach (var y in b.Touches)
                if (TouchesOverlap(x, y))
                    return string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
                        ? $"both touch {x}"
                        : $"{a.ItemKey} touches {x} and {b.ItemKey} touches {y}";
        return null;
    }

    /// <summary>
    /// Whether two touches name overlapping things: equal (ignoring case and the spelling of
    /// separators), one a directory containing the other, a glob matching the other, two globs
    /// whose literal prefixes nest, or the same symbol.
    /// </summary>
    public static bool TouchesOverlap(string x, string y)
    {
        var a = Normalize(x);
        var b = Normalize(y);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        var aGlob = a.Contains('*') || a.Contains('?');
        var bGlob = b.Contains('*') || b.Contains('?');
        if (aGlob && bGlob)
        {
            var pa = LiteralPrefix(a);
            var pb = LiteralPrefix(b);
            return pa.StartsWith(pb, StringComparison.OrdinalIgnoreCase) || pb.StartsWith(pa, StringComparison.OrdinalIgnoreCase);
        }

        if (aGlob)
            return GlobMatches(a, b);
        if (bGlob)
            return GlobMatches(b, a);
        return Contains(a, b) || Contains(b, a);
    }

    private static string Normalize(string touch)
    {
        var path = touch.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    // a is a directory holding b.
    private static bool Contains(string directory, string path) =>
        path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static string LiteralPrefix(string glob)
    {
        var cut = glob.IndexOfAny(['*', '?']);
        return cut < 0 ? glob : glob[..cut];
    }

    private static bool GlobMatches(string glob, string path)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "(/.*)?$";
        return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    // ---- Impact radius ------------------------------------------------------------------------------

    private async Task<string?> FindThroughImpactAsync(
        WorkArea yours, WorkArea theirs, Dictionary<(string Repository, string Touch), Task<HashSet<(string Repository, string Text)>>> cache, CancellationToken ct)
    {
        if (impact is null || yours.Repository is null || theirs.Repository is null)
            return null;

        // What each item's touches reach, in either direction: a touch of one inside the other's blast radius.
        foreach (var (from, to) in new[] { (yours, theirs), (theirs, yours) })
        {
            foreach (var touch in from.Touches.Take(WorkOverlapLimits.MaxTouchesResolved))
            {
                HashSet<(string Repository, string Text)> reached;
                try
                {
                    reached = await ReachedAsync(from.Repository!, touch, cache, ct);
                }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
                {
                    return null;
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "Impact lookup for overlap warnings failed for {Touch}; reporting direct overlaps only.", touch);
                    return null;
                }

                foreach (var other in to.Touches)
                {
                    var hit = reached.FirstOrDefault(node =>
                        string.Equals(node.Repository, to.Repository, StringComparison.OrdinalIgnoreCase) && TouchesOverlap(node.Text, other));
                    if (hit.Text is not null)
                        return $"{from.ItemKey} touches {touch}, whose impact reaches {hit.Text}, which {to.ItemKey} touches ({other})";
                }
            }
        }

        return null;
    }

    private Task<HashSet<(string Repository, string Text)>> ReachedAsync(
        string repository, string touch, Dictionary<(string Repository, string Touch), Task<HashSet<(string Repository, string Text)>>> cache, CancellationToken ct)
    {
        var key = (repository.ToLowerInvariant(), touch);
        if (!cache.TryGetValue(key, out var task))
            cache[key] = task = ResolveAsync(repository, touch, ct);
        return task;
    }

    // The files and symbols in the blast radius of one touch: as a file first, then as a symbol.
    private async Task<HashSet<(string Repository, string Text)>> ResolveAsync(string repository, string touch, CancellationToken ct)
    {
        var path = Normalize(touch);
        var report = await impact!.AnalyzeFileImpactAsync(repository, path, WorkOverlapLimits.ImpactDepth).WaitAsync(ct);
        report ??= await impact.AnalyzeImpactAsync(touch.Trim(), repository, WorkOverlapLimits.ImpactDepth).WaitAsync(ct);

        var reached = new HashSet<(string Repository, string Text)>();
        if (report is null)
            return reached;
        foreach (var node in report.AffectedNodes)
        {
            if (node.FilePath is { Length: > 0 } file)
                reached.Add((node.Project, Normalize(file)));
            reached.Add((node.Project, node.QualifiedName));
        }

        return reached;
    }
}
