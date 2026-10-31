using CodeGraph.Data;

namespace CodeGraph.Services.Pipeline;

/// <summary>Run-local selective route trie. Static misses never enumerate the fleet's
/// route keys; terminal postings also partition repository/origin evidence.</summary>
internal sealed class HttpRouteIndex
{
    private sealed class Branch
    {
        public Dictionary<string, Branch> Static { get; } = new(StringComparer.Ordinal);
        public Branch? Parameter;
        public Bucket? Terminal;
        public Bucket? CatchAll;
    }
    private sealed class Bucket
    {
        public List<LinkNodeRow> Rows { get; } = [];
        private readonly Dictionary<string, List<LinkNodeRow>> _repositories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<LinkNodeRow>> _origins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Repository, string Origin), List<LinkNodeRow>> _scopes = [];
        private static void Add<TKey>(Dictionary<TKey, List<LinkNodeRow>> groups, TKey key, LinkNodeRow row) where TKey : notnull
        { if (!groups.TryGetValue(key, out var rows)) groups[key] = rows = []; rows.Add(row); }
        public void Add(LinkNodeRow row, string? origin)
        {
            Rows.Add(row); Add(_repositories, row.Repository, row);
            Add(_scopes, (row.Repository.ToLowerInvariant(), origin?.ToLowerInvariant() ?? ""), row);
            if (origin is not null) Add(_origins, origin, row);
        }
        public IEnumerable<LinkNodeRow> Candidates(string? repository, string? origin, string? originRepository)
        {
            if (repository is not null)
                return origin is null ? _repositories.GetValueOrDefault(repository) ?? []
                    : (_scopes.GetValueOrDefault((repository.ToLowerInvariant(), origin.ToLowerInvariant())) ?? [])
                        .Concat(_scopes.GetValueOrDefault((repository.ToLowerInvariant(), "")) ?? []);
            if (origin is not null)
                return (_origins.GetValueOrDefault(origin) ?? []).Concat(originRepository is null ? []
                    : _scopes.GetValueOrDefault((originRepository.ToLowerInvariant(), "")) ?? []);
            return Rows;
        }
    }
    private readonly Dictionary<string, Bucket> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Branch> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Repository, string Qn), string?> _origins = [];
    private readonly Dictionary<(string Method, string Url, string? Repository, string? Origin, string? OriginRepository), List<LinkNodeRow>> _matches = [];
    private readonly Dictionary<(string Method, string Template), string> _templates = [];
    private readonly HashSet<(string Repository, string Qn)> _identities = [];
    public HttpBindingBudget Budget { get; }
    public IEnumerable<List<LinkNodeRow>> Values => _keys.Values.Select(bucket => bucket.Rows);
    public HttpRouteIndex(HttpBindingBudget budget) => Budget = budget;
    public void Add(LinkNodeRow route)
    {
        Budget.Text(route.Repository, route.QualifiedName, route.Name); Budget.Properties(route.Properties);
        if (!CrossRepoLinker.IsBackendHttpRoute(route) || !_identities.Add((route.Repository, route.QualifiedName))) return;
        var method = route.Properties.GetValueOrDefault("http_method")!;
        var template = route.Properties.GetValueOrDefault("route_template") ?? "";
        if (!_templates.TryGetValue((method, template), out var key))
            _templates[(method, template)] = key = CrossRepoLinker.NormalizeRouteTemplate(method, template);
        var segments = Segments(key);
        var origin = CrossRepoLinker.CallOrigin(route.Properties, "");
        _origins[(route.Repository, route.QualifiedName)] = origin;
        if (!_keys.TryGetValue(key, out var bucket)) _keys[key] = bucket = new();
        bucket.Add(route, origin);
        var verb = method.ToUpperInvariant();
        if (!_methods.TryGetValue(verb, out var branch)) _methods[verb] = branch = new();
        foreach (var segment in segments)
        {
            Budget.Text(segment); Budget.Charge();
            if (segment == "{**}") { branch.CatchAll ??= new(); branch.CatchAll.Add(route, origin); return; }
            if (segment == "{*}") branch = branch.Parameter ??= new();
            else { if (!branch.Static.TryGetValue(segment, out var next)) branch.Static[segment] = next = new(); branch = next; }
        }
        branch.Terminal = bucket;
    }
    private string[] Segments(string key)
    {
        Budget.Text(key);
        var colon = key.IndexOf(':');
        var depth = 1;
        for (var i = colon + 1; i < key.Length; i++) if (key[i] == '/') depth++;
        Budget.Depth(depth);
        return key[(colon + 1)..].Split('/');
    }
    public string? Origin(LinkNodeRow route) => _origins.GetValueOrDefault((route.Repository, route.QualifiedName));
    public List<LinkNodeRow> Find(string normalizedCall, string method, string url, Func<LinkNodeRow, bool> eligible,
        string? repository, string? origin, string? originRepository, bool cacheKnownEligibility = false)
    {
        Budget.Text(normalizedCall, method, url, repository, origin, originRepository);
        var memo = (method, url, repository, origin, originRepository);
        if (cacheKnownEligibility && _matches.TryGetValue(memo, out var cached)) return cached;
        List<LinkNodeRow> Result(List<LinkNodeRow> result) { if (cacheKnownEligibility) _matches[memo]=result; return result; }
        var template = CrossRepoLinker.NormalizeRouteTemplate(method, url);
        var found = new List<LinkNodeRow>();
        var identities = new HashSet<(string Repository, string Qn)>();
        bool Read(Bucket? bucket)
        {
            if (bucket is null) return false;
            foreach (var row in bucket.Candidates(repository, origin, originRepository))
            {
                Budget.Text(row.Repository, row.QualifiedName); Budget.Charge();
                if (eligible(row) && identities.Add((row.Repository, row.QualifiedName))) found.Add(row);
                if (found.Count == 2) return true; // two distinct eligible endpoints already prove ambiguity
            }
            return found.Count > 0;
        }
        if (Read(_keys.GetValueOrDefault(template)) || Read(_keys.GetValueOrDefault(normalizedCall)))
            return Result(found);
        var segments = Segments(template);
        if (!_methods.TryGetValue(method.ToUpperInvariant(), out var root)) return Result(found);
        var stack = new Stack<(Branch Branch, int Offset)>(); stack.Push((root, 0));
        while (stack.TryPop(out var state))
        {
            Budget.Charge();
            if (Read(state.Branch.CatchAll) && found.Count >= 2) break;
            if (state.Offset == segments.Length) { if (Read(state.Branch.Terminal) && found.Count >= 2) break; continue; }
            var segment = segments[state.Offset]; Budget.Text(segment);
            if (state.Branch.Parameter is { } parameter) stack.Push((parameter, state.Offset + 1));
            if (state.Branch.Static.TryGetValue(segment, out var next)) stack.Push((next, state.Offset + 1));
        }
        return Result(found);
    }
}
