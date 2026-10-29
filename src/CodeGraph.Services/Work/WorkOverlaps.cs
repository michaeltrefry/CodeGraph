using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CodeGraph.Models.Work;
using CodeGraph.Services.Analyzers;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Work;

/// <summary>
/// A short-lived, size-bounded, process-wide cache of what a touched file or symbol reaches in the
/// graph, shared by every <see cref="WorkOverlaps"/> (register it as a singleton). Negative
/// results and failed lookups are cached too, so a repeated work-tool call does not walk the graph
/// again within <see cref="Ttl"/>; a lookup cut short by a deadline is not cached.
/// </summary>
public sealed class OverlapImpactCache(TimeProvider? clock = null, int maxEntries = 2_000, TimeSpan? ttl = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Repository, string Touch, int Depth), (DateTimeOffset Expires, HashSet<(string Repository, string Text)> Reached)> entries = new();

    public TimeSpan Ttl { get; } = ttl ?? TimeSpan.FromSeconds(60);

    public int Count => entries.Count;

    public bool TryGet((string Repository, string Touch, int Depth) key, out HashSet<(string Repository, string Text)> reached)
    {
        if (entries.TryGetValue(key, out var entry) && entry.Expires > clock.GetUtcNow())
        {
            reached = entry.Reached;
            return true;
        }

        reached = [];
        return false;
    }

    public void Set((string Repository, string Touch, int Depth) key, HashSet<(string Repository, string Text)> reached)
    {
        if (entries.Count >= maxEntries)
        {
            // Bounded: drop the lapsed entries, and if the cache is still full, the whole of it.
            var now = clock.GetUtcNow();
            foreach (var stale in entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
                entries.TryRemove(stale, out _);
            if (entries.Count >= maxEntries)
                entries.Clear();
        }

        entries[key] = (clock.GetUtcNow() + Ttl, reached);
    }
}

/// <summary>
/// Overlap of the work areas of live sessions (design §5.6, R21): the claimed items' touches
/// (paths, directories, globs, symbols) overlap directly, or one touches something inside the
/// other's impact radius, taken from the same <see cref="IImpactAnalysisService"/> that backs
/// analyze_impact and scoped to the item's repository. Advisory: it only informs (E6).
/// <list type="bullet">
/// <item>Direct overlaps are always computed, whatever the deadline.</item>
/// <item>The graph is consulted only for pairs that do not overlap directly, to depth
/// <see cref="WorkOverlapLimits.ImpactDepth"/>, for at most <see cref="WorkOverlapLimits.MaxTouchesResolved"/>
/// touches per item, one lookup at a time (the graph store's context is not safe for concurrent
/// use), and each lookup is checked against the deadline before it starts and abandoned at it.
/// Results are cached across calls, failures and misses included; past the deadline, or on a
/// graph failure, only what is already known is reported.</item>
/// <item>Impact is the dependents of what a touch names (who calls, uses, inherits or consumes it,
/// two hops), not what it depends on: a touch of one item inside the other's blast radius, either way round.</item>
/// <item>An item with no repository is compared directly with every item whatever its repository
/// (its paths cannot be told apart), and is never looked up in the graph.</item>
/// </list>
/// </summary>
public sealed class WorkOverlaps(IImpactAnalysisService? impact = null, OverlapImpactCache? sharedCache = null, ILogger<WorkOverlaps>? logger = null)
{
    private readonly OverlapImpactCache cache = sharedCache ?? new OverlapImpactCache();

    /// <summary>Whether overlaps through the impact radius can be found at all (an impact service is wired).</summary>
    public bool HasImpact => impact is not null;

    public OverlapImpactCache Cache => cache;

    /// <summary>
    /// The warnings for <paramref name="sessionId"/> against every other session's area, in item
    /// order, computed within <paramref name="deadline"/> (direct overlaps are never cut).
    /// </summary>
    public async Task<IReadOnlyList<OverlapWarning>> WarningsForAsync(string sessionId, IReadOnlyList<WorkArea> areas, TimeSpan? deadline = null, CancellationToken ct = default)
    {
        var mine = areas.Where(area => area.SessionId == sessionId).ToList();
        var others = areas.Where(area => area.SessionId != sessionId).ToList();
        if (mine.Count == 0 || others.Count == 0)
            return [];

        var found = new List<(WorkArea Yours, WorkArea Theirs, string Kind, string Via)>();
        var remaining = new List<(WorkArea Yours, WorkArea Theirs)>();
        foreach (var yours in mine)
        {
            foreach (var theirs in others)
            {
                if (FindDirect(yours, theirs) is { } direct)
                    found.Add((yours, theirs, OverlapKinds.Direct, direct));
                else
                    remaining.Add((yours, theirs));
            }
        }

        if (impact is not null && remaining.Count > 0 && found.Count < WorkOverlapLimits.MaxWarnings)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(deadline ?? TimeSpan.FromMilliseconds(WorkOverlapLimits.ImpactBudgetMilliseconds));
            var resolved = new Dictionary<(string Repository, string Touch), HashSet<(string Repository, string Text)>>();
            foreach (var (yours, theirs) in remaining)
            {
                if (budget.IsCancellationRequested || found.Count >= WorkOverlapLimits.MaxWarnings)
                    break;
                if (await FindThroughImpactAsync(yours, theirs, resolved, budget.Token) is { } through)
                    found.Add((yours, theirs, OverlapKinds.Impact, through));
            }
        }

        return found
            .OrderBy(entry => entry.Yours.ItemKey, StringComparer.Ordinal).ThenBy(entry => entry.Theirs.ItemKey, StringComparer.Ordinal)
            .Take(WorkOverlapLimits.MaxWarnings)
            .Select(entry => Warn(entry.Kind, entry.Yours, entry.Theirs, entry.Via))
            .ToList();
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
    /// whose literal prefixes nest, or symbols that are the same or nest (<c>App.Greeter</c> holds
    /// <c>App.Greeter.Greet</c>; <c>a::b</c> holds <c>a::b::c</c>).
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
        return Contains(a, b) || Contains(b, a) || NestedSymbols(a, b) || NestedSymbols(b, a);
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

    // Symbols (no path separator): outer is a qualified-name prefix of inner.
    private static bool NestedSymbols(string outer, string inner) =>
        !outer.Contains('/') && !inner.Contains('/') &&
        (inner.StartsWith(outer + ".", StringComparison.OrdinalIgnoreCase) || inner.StartsWith(outer + "::", StringComparison.OrdinalIgnoreCase));

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
        WorkArea yours, WorkArea theirs, Dictionary<(string Repository, string Touch), HashSet<(string Repository, string Text)>> resolved, CancellationToken ct)
    {
        if (yours.Repository is null || theirs.Repository is null)
            return null;

        // A touch of one inside the blast radius of a touch of the other, either way round.
        foreach (var (from, to) in new[] { (yours, theirs), (theirs, yours) })
        {
            foreach (var touch in from.Touches.Take(WorkOverlapLimits.MaxTouchesResolved))
            {
                var reached = await ReachedAsync(from.Repository!, touch, resolved, ct);
                if (reached is null)
                    return null;

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

    // Null when the budget ran out before the lookup could finish: nothing is known, nothing cached.
    private async Task<HashSet<(string Repository, string Text)>?> ReachedAsync(
        string repository, string touch, Dictionary<(string Repository, string Touch), HashSet<(string Repository, string Text)>> resolved, CancellationToken ct)
    {
        var local = (repository.ToLowerInvariant(), touch);
        if (resolved.TryGetValue(local, out var known))
            return known;

        var key = (local.Item1, touch, WorkOverlapLimits.ImpactDepth);
        if (cache.TryGet(key, out var cached))
            return resolved[local] = cached;

        // An expired budget stops work before the next lookup begins.
        if (ct.IsCancellationRequested)
            return null;
        try
        {
            var reached = await LookUpAsync(repository, touch, ct);
            cache.Set(key, reached);
            return resolved[local] = reached;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            // A failed lookup is remembered as reaching nothing, so a broken graph is not retried on every call.
            logger?.LogWarning(exception, "Impact lookup for overlap warnings failed for {Touch}; reporting what is known.", touch);
            cache.Set(key, []);
            return resolved[local] = [];
        }
    }

    // The files and symbols in the blast radius of one touch: as a file first, then as a symbol.
    // The impact service takes no token, so a lookup past the budget is abandoned, not cancelled.
    private async Task<HashSet<(string Repository, string Text)>> LookUpAsync(string repository, string touch, CancellationToken ct)
    {
        var path = Normalize(touch);
        var report = await impact!.AnalyzeFileImpactAsync(repository, path, WorkOverlapLimits.ImpactDepth).WaitAsync(ct);
        if (report is null && !ct.IsCancellationRequested)
            report = await impact.AnalyzeImpactAsync(touch.Trim(), repository, WorkOverlapLimits.ImpactDepth).WaitAsync(ct);

        var reached = new HashSet<(string Repository, string Text)>();
        if (report is null)
            return reached;
        foreach (var node in report.AffectedNodes)
        {
            if (node.FilePath is { Length: > 0 } file)
                reached.Add((node.Repository, Normalize(file)));
            reached.Add((node.Repository, node.QualifiedName));
        }

        return reached;
    }
}
