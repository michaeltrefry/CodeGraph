using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>Source-only Tauri registration analysis over the discovery-admitted input map.
/// Function identity always comes from the Rust extractor's declaration at the exact file/span.
/// No Cargo, Rust macro, configuration, command or application code is executed.</summary>
internal static partial class TauriOperationAnalysis
{
    internal const string Owner = "tauri";
    internal sealed record Analysis(ExtractionResult Result, IReadOnlyList<string> Diagnostics, long WorkUnits = 0);
    internal const long DefaultMaxWork = 20_000_000;
    internal const int DefaultMaxDepth = 128;
    private sealed record Token(string Text, int Offset, bool String = false);
    // Line is the function name's declaration line, matching SCIP name occurrences. Source
    // offsets retain the full lexical body independently of the native extractor's spans.
    private sealed record Definition(string Path, string Module, string Name, string ExposedName, int Line, int StartOffset, int EndOffset, bool Command, Dictionary<string, string> Imports);
    private sealed record Constant(string Module, string Name, List<Token> Value, Dictionary<string, string> Imports);
    private sealed record Registration(string Path, string Module, string Target, string? Plugin, int Line, Dictionary<string, string> Imports);
    private sealed record RustFile(string Path, string Crate, string CrateName, List<Token> Tokens,
        Dictionary<string, string> Imports, List<Definition> Definitions, List<Registration> Registrations, List<Constant> Constants);
    private sealed record App(string Root, string Frontend, string Config);

    internal static Analysis Analyze(string repository, IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<GraphNode> declarations, IReadOnlyList<GraphNode> references,
        CancellationToken ct = default, long maxWork = DefaultMaxWork, int maxDepth = DefaultMaxDepth) =>
        new Run(ct, maxWork, maxDepth).Execute(repository, sources, declarations, references);

    private sealed class WorkLimitException(string message) : Exception(message);

    // Every cache and counter belongs to one invocation. Work is charged before bulk scans,
    // or per examined character/token/candidate, independently of wall-clock timing.
    private sealed class Run(CancellationToken ct, long maxWork, int maxDepth)
    {
        private long _work;
        private readonly List<string> _diagnostics = [];
        private readonly Dictionary<string, int[]> _lines = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<List<Token>, int[]> _delimiters = [];
        private readonly Dictionary<List<Token>, SpanIndex<Definition>> _syntaxDefinitions = [];
        private readonly Dictionary<(string Path, string Name), SpanIndex<GraphNode>> _functions = [];
        private void Charge(long amount = 1)
        {
            ct.ThrowIfCancellationRequested();
            if (amount < 0 || amount > Math.Max(0, maxWork) - _work)
                throw new WorkLimitException($"Tauri analysis work budget exhausted ({maxWork} units)");
            _work += amount;
        }
        private void Depth(int depth)
        {
            Charge();
            var limit = Math.Clamp(maxDepth, 0, 512);
            if (depth > limit) throw new WorkLimitException($"Tauri analysis depth limit exceeded ({limit})");
        }
        private void Diagnostic(Func<string> message)
        {
            if (_diagnostics.Count >= 98) return;
            var text = message();
            Charge(text.Length);
            _diagnostics.Add(text.Length > 2048 ? text[..2048] : text);
        }
        private sealed class SpanIndex<T> where T : class
        {
            private sealed record Branch(T Value, int Start, int End, int MaximumEnd, Branch? Left, Branch? Right);
            private readonly Branch? _root;
            private readonly Run _run;
            internal SpanIndex(Run run, List<T> values, Func<T, int> start, Func<T, int> end)
            {
                _run = run;
                run.Charge((long)values.Count * (2 + 2 * System.Numerics.BitOperations.Log2((uint)Math.Max(1, values.Count))));
                var ordered = values.OrderBy(start).ThenBy(end).ToArray();
                _root = Build(0, ordered.Length, 1);
                Branch? Build(int from, int until, int depth)
                {
                    if (from >= until) return null;
                    run.Depth(depth);
                    var middle = from + (until - from) / 2;
                    var left = Build(from, middle, depth + 1); var right = Build(middle + 1, until, depth + 1);
                    var value = ordered[middle]; var last = end(value);
                    return new Branch(value, start(value), last, Math.Max(last, Math.Max(left?.MaximumEnd ?? int.MinValue, right?.MaximumEnd ?? int.MinValue)), left, right);
                }
            }
            internal T? Smallest(int line, bool requireUnique = false)
            {
                var pending = new Stack<Branch>(); if (_root is not null) pending.Push(_root);
                T? best = default; var length = long.MaxValue; var found = false; var tied = false;
                while (pending.TryPop(out var branch))
                {
                    _run.Charge();
                    if (branch.MaximumEnd < line) continue;
                    if (branch.Left is not null) pending.Push(branch.Left);
                    if (branch.Start > line) continue;
                    if (branch.Right is not null) pending.Push(branch.Right);
                    if (branch.End < line) continue;
                    var span = (long)branch.End - branch.Start;
                    if (span < length) { best = branch.Value; length = span; tied = false; found = true; }
                    else if (span == length) tied = true;
                }
                return found && !(requireUnique && tied) ? best : default;
            }
        }
        private void IndexDeclarations(IReadOnlyList<GraphNode> declarations, HashSet<string> nativePaths)
        {
            var names = new Dictionary<(string, string), List<GraphNode>>();
            var participatingPaths = new Dictionary<string, bool>(ReferenceEqualityComparer.Instance);
            foreach (var node in declarations)
            {
                Charge();
                if (node.Label is not (NodeLabel.Function or NodeLabel.Method)) continue;
                // Extractors share one path string across a source file's declarations. Remember
                // membership by reference before hashing text again; unrelated repository symbols
                // never enter name/span indexes or incur name/qualified-name processing.
                if (!participatingPaths.TryGetValue(node.FilePath, out var participating))
                {
                    Charge(node.FilePath.Length);
                    participatingPaths[node.FilePath] = participating = nativePaths.Contains(node.FilePath);
                }
                if (!participating) continue;
                Charge(node.FilePath.Length + node.Name.Length);
                if (!names.TryGetValue((node.FilePath, node.Name), out var named)) names[(node.FilePath, node.Name)] = named = [];
                named.Add(node);
            }
            foreach (var (key, values) in names) { Charge(); _functions[key] = new SpanIndex<GraphNode>(this, values, node => node.StartLine, node => node.EndLine); }
        }
        internal Analysis Execute(string repository, IReadOnlyDictionary<string, string> sources,
            IReadOnlyList<GraphNode> declarations, IReadOnlyList<GraphNode> references)
        {
            try { Charge(0); return AnalyzeCore(repository, sources, declarations, references); }
            catch (WorkLimitException ex)
            {
                _diagnostics.Add("tauriIncomplete: " + ex.Message + "; prior graph and source hashes must be preserved");
                return new Analysis(new ExtractionResult { Complete = false }, _diagnostics, _work);
            }
        }
        private Analysis AnalyzeCore(string repository, IReadOnlyDictionary<string, string> sources,
            IReadOnlyList<GraphNode> declarations, IReadOnlyList<GraphNode> references)
        {
            var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            var edges = new List<PendingEdge>();
            var complete = true;
            var apps = new List<App>();
            foreach (var path in sources.Keys) Charge(path.Length * 8L + 1);
            var manifests = sources.Keys.Where(path => path.EndsWith("Cargo.toml", StringComparison.Ordinal)).ToList();
            var manifestRoots = manifests.Select(DirectoryPart).ToHashSet(StringComparer.Ordinal);
            var packages = sources.Keys.Where(path => path.EndsWith("package.json", StringComparison.Ordinal)).Select(DirectoryPart).ToHashSet(StringComparer.Ordinal);
            foreach (var config in sources.Keys.Where(path => Path.GetFileName(path) == "tauri.conf.json"))
            {
                try
                {
                    Charge(sources[config].Length);
                    using var json = JsonDocument.Parse(sources[config], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    Charge();
                    var root = DirectoryPart(config);
                    if (!manifestRoots.Contains(root))
                    {
                        Diagnostic(() => $"tauriIncomplete: {config} has no admitted Cargo.toml"); complete = false; continue;
                    }
                    string? dist = null;
                    if (json.RootElement.TryGetProperty("build", out var build))
                        foreach (var key in new[] { "frontendDist", "distDir" })
                            if (build.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) dist = value.GetString();
                    var frontend = DirectoryPart(root);
                    if (dist is not null && !dist.Contains("://", StringComparison.Ordinal))
                    {
                        var output = Join(root, dist);
                        frontend = NearestRoot(output, packages) ?? DirectoryPart(output);
                    }
                    apps.Add(new App(root, frontend, config));
                }
                catch (JsonException ex) { Diagnostic(() => $"tauriIncomplete: {config}: {ex.Message}"); complete = false; }
            }
            if (sources.Keys.Any(path => path.EndsWith("tauri.conf.json5", StringComparison.Ordinal) || path.EndsWith("Tauri.toml", StringComparison.Ordinal)))
            {
                Diagnostic(() => "tauriIncomplete: non-JSON Tauri configuration is not statically readable"); complete = false;
            }

            var rustFiles = new List<RustFile>();
            var crateNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, source) in sources.Where(pair => pair.Key.EndsWith(".rs", StringComparison.Ordinal)))
            {
                Charge();
                var crate = NearestRoot(path, manifestRoots);
                if (crate is null) continue;
                if (!crateNames.TryGetValue(crate, out var crateName))
                {
                    var text = sources[Join(crate, "Cargo.toml")]; Charge(text.Length);
                    crateNames[crate] = crateName = Regex.Match(text, "(?m)^\\s*name\\s*=\\s*\"([^\"]+)\"", RegexOptions.NonBacktracking).Groups[1].Value.Replace('-', '_');
                }
                var tokens = Lex(source, out var lexicalError);
                if (lexicalError is not null) { Diagnostic(() => $"tauriIncomplete: {lexicalError} in {path}"); complete = false; continue; }
                if (!Balanced(tokens)) { Diagnostic(() => $"tauriIncomplete: unbalanced Rust input {path}"); complete = false; continue; }
                var imports = Imports(tokens);
                var definitions = new List<Definition>();
                var registrations = new List<Registration>();
                var constants = new List<Constant>();
                var relative = path[(crate.Length == 0 ? 0 : crate.Length + 1)..];
                var module = ModuleFor(relative);
                Parse(tokens, 0, tokens.Count, module, path, source, imports, definitions, registrations, constants);
                _syntaxDefinitions[tokens] = new SpanIndex<Definition>(this, definitions, definition => definition.StartOffset, definition => definition.EndOffset);
                rustFiles.Add(new RustFile(path, crate, crateName, tokens, imports, definitions, registrations, constants));
            }
            IndexDeclarations(declarations, rustFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal));
            var filesByCrate = rustFiles.GroupBy(file => file.Crate).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var frontendOwners = apps.GroupBy(app => app.Frontend).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var frontendRoots = frontendOwners.Keys.ToHashSet(StringComparer.Ordinal);
            var appReferences = new Dictionary<App, List<GraphNode>>();
            foreach (var reference in references)
            {
                Charge(reference.FilePath.Length + 1);
                var root = NearestRoot(reference.FilePath, frontendRoots);
                if (root is null) continue;
                var owners = frontendOwners[root];
                if (owners.Count != 1) { Diagnostic(() => $"tauriUnresolved: ambiguous app for {reference.FilePath}"); continue; }
                if (!appReferences.TryGetValue(owners[0], out var list)) appReferences[owners[0]] = list = [];
                list.Add(reference);
            }
            foreach (var app in apps)
            {
                Charge();
                var files = filesByCrate.GetValueOrDefault(app.Root) ?? [];
                var commandDefinitions = new Dictionary<string, List<Definition>>(StringComparer.Ordinal);
                var constantDefinitions = new Dictionary<string, List<Constant>>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    foreach (var definition in file.Definitions)
                    {
                        Charge(definition.Module.Length + definition.Name.Length + 1);
                        if (!definition.Command) continue;
                        var key = JoinModule(definition.Module, definition.Name);
                        if (!commandDefinitions.TryGetValue(key, out var values)) commandDefinitions[key] = values = [];
                        values.Add(definition);
                    }
                    foreach (var constant in file.Constants)
                    {
                        Charge(constant.Module.Length + constant.Name.Length + 1);
                        var key = JoinModule(constant.Module, constant.Name);
                        if (!constantDefinitions.TryGetValue(key, out var values)) constantDefinitions[key] = values = [];
                        values.Add(constant);
                    }
                }
                var registered = new Dictionary<string, List<(Definition Definition, GraphNode Function, Registration Registration)>>(StringComparer.Ordinal);
                foreach (var file in files) foreach (var registration in file.Registrations)
                {
                    Charge();
                    var path = ResolvePath(registration.Target, registration.Module, registration.Imports, file.CrateName);
                    Charge(path.Length);
                    var candidates = commandDefinitions.GetValueOrDefault(path) ?? [];
                    if (candidates.Count != 1)
                    {
                        Diagnostic(() => $"tauriIncomplete: registration {registration.Target} in {file.Path}:{registration.Line} resolves to {candidates.Count} admitted command declarations");
                        complete = false; continue;
                    }
                    var definition = candidates[0];
                    var function = FindFunction(definition.Path, definition.Name, definition.Line);
                    if (function is null)
                    {
                        Diagnostic(() => $"tauriIncomplete: Rust declaration unavailable for registered command {definition.Path}:{definition.Line} {definition.Name}");
                        complete = false; continue;
                    }
                    Charge((registration.Plugin?.Length ?? 0) + definition.ExposedName.Length + 10L);
                    var exposed = registration.Plugin is null ? definition.ExposedName : $"plugin:{registration.Plugin}|{definition.ExposedName}";
                    if (!registered.TryGetValue(exposed, out var list)) registered[exposed] = list = [];
                    list.Add((definition, function, registration));
                }
                foreach (var (name, targets) in registered)
                {
                    foreach (var candidate in targets) Charge(candidate.Function.QualifiedName.Length + 1);
                    if (targets.Select(target => target.Function.QualifiedName).Distinct().Count() != 1)
                    {
                        Diagnostic(() => $"tauriUnresolved: ambiguous registration {app.Root}:{name}"); continue;
                    }
                    var target = targets[0];
                    var commandQn = Identity(repository, app.Root, "command", name);
                    nodes[commandQn] = NewNode(NodeLabel.Function, commandQn, name, target.Registration.Path, new()
                    {
                        ["kind"] = "tauri_command", ["app_root"] = app.Root, ["frontend_root"] = app.Frontend,
                        ["registration_file"] = target.Registration.Path, ["registration_line"] = target.Registration.Line,
                        ["native_file"] = target.Definition.Path, ["native_function"] = target.Function.QualifiedName,
                        ["confidence"] = "high"
                    });
                    edges.Add(Edge(commandQn, target.Function.QualifiedName, EdgeType.CALLS, target.Registration.Path));
                }

                foreach (var reference in appReferences.GetValueOrDefault(app) ?? [])
                {
                    Charge();
                    var name = Text(reference.Properties.GetValueOrDefault("tauri_name"));
                    if (string.IsNullOrEmpty(name)) continue;
                    var kind = Text(reference.Properties.GetValueOrDefault("kind"));
                    if (kind == "tauri_command_reference")
                    {
                        var target = Identity(repository, app.Root, "command", name);
                        if (nodes.ContainsKey(target)) edges.Add(Edge(reference.QualifiedName, target, EdgeType.CALLS, reference.FilePath));
                        else Diagnostic(() => $"tauriUnresolved: command {name} is not uniquely registered in {app.Root}");
                    }
                    else if (kind == "tauri_event_reference")
                    {
                        var target = Event(app, name);
                        var api = Text(reference.Properties.GetValueOrDefault("tauri_api"));
                        var caller = Text(reference.Properties.GetValueOrDefault("tauri_caller"));
                        if (caller is not null) edges.Add(Edge(caller, target, api is "listen" or "once" ? EdgeType.SUBSCRIBES : EdgeType.PUBLISHES, reference.FilePath));
                        var callback = Text(reference.Properties.GetValueOrDefault("tauri_callback"));
                        if (callback is not null) edges.Add(Edge(target, callback, EdgeType.HANDLES, app.Config));
                    }
                }
                foreach (var file in files)
                {
                    Charge(file.Imports.Count + 1);
                    // Tauri Emitter import is required evidence; unrelated .emit calls are not events.
                    if (!file.Imports.Values.Any(value => value is "tauri::Emitter" or "tauri::Manager")) continue;
                    for (var index = 1; index + 3 < file.Tokens.Count; index++)
                    {
                        Charge();
                        if (file.Tokens[index].String) continue;
                        if (file.Tokens[index - 1].Text != "." || file.Tokens[index].Text is not ("emit" or "emit_all" or "emit_to") || file.Tokens[index + 1].Text != "(") continue;
                        if (index < 2 || !IsTauriEmitter(file.Tokens, index - 2)) continue;
                        var nameIndex = index + 2;
                        if (file.Tokens[index].Text == "emit_to")
                        {
                            while (nameIndex < file.Tokens.Count && file.Tokens[nameIndex].Text != ",")
                            {
                                Charge();
                                if (!file.Tokens[nameIndex].String && file.Tokens[nameIndex].Text is "(" or "[" or "{") nameIndex = Close(file.Tokens, nameIndex);
                                nameIndex++;
                            }
                            nameIndex++;
                        }
                        var declaration = _syntaxDefinitions[file.Tokens].Smallest(file.Tokens[index].Offset, requireUnique: true);
                        var eventName = StaticEventName(file, constantDefinitions, nameIndex, declaration);
                        if (eventName is null)
                        { Diagnostic(() => $"tauriUnresolved: dynamic Rust event in {file.Path}:{Line(sources[file.Path], file.Tokens[index].Offset)}"); continue; }
                        var line = Line(sources[file.Path], file.Tokens[index].Offset);
                        var function = declaration is null ? null : FindFunction(declaration.Path, declaration.Name, declaration.Line);
                        if (function is null) { Diagnostic(() => $"tauriIncomplete: Rust event producer unavailable at {file.Path}:{line}"); complete = false; continue; }
                        edges.Add(Edge(function.QualifiedName, Event(app, eventName), EdgeType.PUBLISHES, file.Path));
                    }
                }
            }
            Charge(nodes.Count * 3L + edges.Count);
            _diagnostics.Add($"tauriCoverage: apps={apps.Count}, commands={nodes.Values.Count(node => Text(node.Properties.GetValueOrDefault("kind")) == "tauri_command")}, events={nodes.Values.Count(node => node.Label == NodeLabel.Event)}, bindings={edges.Count}, complete={complete}");
            return new Analysis(new ExtractionResult { Complete = complete, Nodes = nodes.Values.ToList(), Edges = edges }, _diagnostics, _work);

            string Event(App app, string name)
            {
                var qn = Identity(repository, app.Root, "event", name);
                nodes.TryAdd(qn, NewNode(NodeLabel.Event, qn, name, app.Config, new() { ["kind"] = "tauri_event", ["app_root"] = app.Root }));
                return qn;
            }
            GraphNode NewNode(NodeLabel label, string qn, string name, string owner, Dictionary<string, object> properties)
            {
                properties["transport"] = "tauri"; properties["extraction_owner"] = Owner;
                return new GraphNode { Repository = repository, Label = label, Name = name, QualifiedName = qn, FilePath = owner, Properties = properties };
            }
        }

        private GraphNode? FindFunction(string path, string name, int line)
        {
            Charge(path.Length + name.Length + 1);
            return _functions.GetValueOrDefault((path, name))?.Smallest(line, requireUnique: true);
        }
        private bool IsTauriEmitter(List<Token> tokens, int receiver)
        {
            var name = tokens[receiver].Text;
            // The selected apps pass a Tauri AppHandle/Window into producers. Require that typed
            // declaration in the enclosing function; importing Emitter alone does not prove that
            // an unrelated object's identically named emit method is a Tauri event.
            for (var start = receiver; start >= 0; start--)
            {
                Charge();
                if (tokens[start].String || tokens[start].Text != "fn") continue;
                var parameters = start + 2;
                while (parameters < receiver && tokens[parameters].Text != "(") { Charge(); parameters++; }
                var end = Close(tokens, parameters);
                if (end >= receiver) continue;
                for (var cursor = end + 1; cursor + 1 < receiver; cursor++)
                {
                    Charge();
                    if (tokens[cursor].Text == "let" && (Equal(tokens[cursor + 1].Text, name)
                        || tokens[cursor + 1].Text == "mut" && cursor + 2 < receiver && Equal(tokens[cursor + 2].Text, name))) return false;
                }
                for (var index = parameters; index + 2 < end; index++)
                {
                    Charge();
                    if (!Equal(tokens[index].Text, name) || tokens[index + 1].Text != ":") continue;
                    var typeEnd = index + 2; var tauri = false;
                    while (typeEnd < end && tokens[typeEnd].Text != ",")
                    { Charge(); tauri |= tokens[typeEnd].Text is "AppHandle" or "Window" or "WebviewWindow" or "App"; typeEnd++; }
                    return tauri;
                }
                return false;
            }
            return false;
        }
        private string? StaticEventName(RustFile file, Dictionary<string, List<Constant>> constants, int start, Definition? enclosing)
        {
            if (start >= file.Tokens.Count) return null;
            if (file.Tokens[start].String) return file.Tokens[start].Text;
            var end = start; while (end < file.Tokens.Count && file.Tokens[end].Text is not ("," or ")")) { Charge(); end++; }
            var target = TokenText(file.Tokens, start, end - start);
            if (!QualifiedPath(target)) return null;
            // Parameters/locals can shadow constants. Look only within this producer's lexical body.
            var functionStart = start;
            while (functionStart > 0 && (file.Tokens[functionStart].String || file.Tokens[functionStart].Text != "fn")) { Charge(); functionStart--; }
            for (var index = functionStart; index + 1 < start; index++)
            {
                Charge();
                if ((file.Tokens[index].Text is "let" or "mut" && Equal(file.Tokens[index + 1].Text, target))
                    || Equal(file.Tokens[index].Text, target) && file.Tokens[index + 1].Text == ":") return null;
            }
            var module = enclosing?.Module ?? ModuleFor(file.Path[(file.Crate.Length == 0 ? 0 : file.Crate.Length + 1)..]);
            return Resolve(target, module, enclosing?.Imports ?? file.Imports, new HashSet<string>(StringComparer.Ordinal));

            string? Resolve(string name, string owner, Dictionary<string, string> imports, HashSet<string> seen)
            {
                var resolved = ResolvePath(name, owner, imports, file.CrateName);
                Charge(resolved.Length);
                Depth(seen.Count + 1);
                if (!seen.Add(resolved)) return null;
                var matches = constants.GetValueOrDefault(resolved) ?? [];
                if (matches.Count != 1) return null;
                var constant = matches[0];
                if (constant.Value.Count == 1 && constant.Value[0].String) return constant.Value[0].Text;
                var alias = TokenText(constant.Value, 0, constant.Value.Count);
                return QualifiedPath(alias) ? Resolve(alias, constant.Module, constant.Imports, seen) : null;
            }
        }
        private PendingEdge Edge(string source, string target, EdgeType type, string owner) => new(source, target, type,
            new() { ["extraction_owner"] = Owner, ["extraction_source_file"] = owner, ["transport"] = "tauri", ["confidence"] = "high" });
        private string Identity(string repo, string app, string kind, string name)
        { Charge(repo.Length + app.Length + kind.Length + name.Length + 1); return $"tauri:{Uri.EscapeDataString(repo)}:{Uri.EscapeDataString(app)}:{kind}:{Uri.EscapeDataString(name)}"; }
        private string? Text(object? value) => value is JsonElement json ? json.GetString() : value?.ToString();
        private bool Under(string path, string root) => root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        private string DirectoryPart(string path) => Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        private string Join(string root, string path) => Path.GetRelativePath("/", Path.GetFullPath(Path.Combine("/", root, path))).Replace('\\', '/');
        private string JoinModule(string module, string name)
        { Charge(module.Length + name.Length + 1); return module.Length == 0 ? name : module + "::" + name; }
        private string ModuleFor(string path)
        {
            var parts = path.Replace('\\', '/').Split('/').ToList();
            if (parts[0] == "src") parts.RemoveAt(0);
            parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
            if (parts[^1] is "main" or "lib" or "mod") parts.RemoveAt(parts.Count - 1);
            return string.Join("::", parts);
        }
        private int Line(string source, int offset)
        {
            if (!_lines.TryGetValue(source, out var starts))
            {
                var lines = new List<int> { 0 };
                for (var index = 0; index < source.Length; index++) { Charge(); if (source[index] == '\n') lines.Add(index + 1); }
                _lines[source] = starts = lines.ToArray();
            }
            Charge(1 + System.Numerics.BitOperations.Log2((uint)Math.Max(1, starts.Length)));
            var found = Array.BinarySearch(starts, offset);
            return found >= 0 ? found + 1 : ~found;
        }
        private string? NearestRoot(string path, IReadOnlySet<string> roots)
        {
            var candidate = path;
            while (true)
            {
                Charge(candidate.Length + 1);
                if (roots.Contains(candidate)) return candidate;
                if (candidate.Length == 0) return null;
                candidate = DirectoryPart(candidate);
            }
        }
        private bool QualifiedPath(string value)
        {
            Charge(value.Length + 1);
            return Regex.IsMatch(value, "^(?:[A-Za-z_][A-Za-z0-9_]*::)*[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.NonBacktracking);
        }
        private bool Equal(string left, string right)
        {
            Charge(left.Length == right.Length ? left.Length + 1L : 1);
            return string.Equals(left, right, StringComparison.Ordinal);
        }
        private string TokenText(List<Token> tokens, int from, int count)
        {
            var text = new System.Text.StringBuilder();
            for (var index = from; index < from + count; index++) { Charge(tokens[index].Text.Length + 1); text.Append(tokens[index].Text); }
            return text.ToString();
        }
        private List<Token> TokenSlice(List<Token> tokens, int from, int count)
        {
            Charge(count);
            return tokens.GetRange(from, count);
        }
        private int FindToken(List<Token> tokens, int from, int count, string value)
        {
            for (var index = from; index < from + count; index++) { Charge(); if (!tokens[index].String && tokens[index].Text == value) return index; }
            return -1;
        }
        private int FindText(string source, string value, int from)
        {
            for (var index = from; index <= source.Length - value.Length; index++)
            {
                var matched = 0;
                while (matched < value.Length) { Charge(); if (source[index + matched] != value[matched]) break; matched++; }
                if (matched == value.Length) return index;
            }
            return -1;
        }

        private string ResolvePath(string target, string module, Dictionary<string, string> imports, string crate)
        {
            Charge(target.Length + module.Length + crate.Length + 1);
            var parts = target.Split("::").ToList();
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            while (parts.Count > 0 && imports.TryGetValue(parts[0], out var import) && aliases.Add(parts[0]))
            {
                Depth(aliases.Count);
                Charge(import.Length + PartsLength(parts));
                parts = import.Split("::").Concat(parts.Skip(1)).ToList();
            }
            if (parts[0] is "crate" || parts[0] == crate) parts.RemoveAt(0);
            else if (parts[0] == "self") { parts.RemoveAt(0); parts = module.Split("::", StringSplitOptions.RemoveEmptyEntries).Concat(parts).ToList(); }
            else if (parts[0] == "super")
            {
                var parent = module.Split("::", StringSplitOptions.RemoveEmptyEntries).ToList();
                while (parts.Count > 0 && parts[0] == "super") { Charge(parts.Count); parts.RemoveAt(0); if (parent.Count > 0) parent.RemoveAt(parent.Count - 1); }
                parts = parent.Concat(parts).ToList();
            }
            else if (module.Length > 0) parts = module.Split("::").Concat(parts).ToList();
            Charge(PartsLength(parts));
            return string.Join("::", parts);
        }
        private long PartsLength(List<string> parts)
        {
            long length = 0;
            foreach (var part in parts) { Charge(); length += part.Length + 2L; }
            return length;
        }

        private void Parse(List<Token> tokens, int from, int end, string module, string path, string source,
            Dictionary<string, string> imports, List<Definition> definitions, List<Registration> registrations, List<Constant> constants, int depth = 1)
        {
            Depth(depth);
            imports = Imports(tokens, from, end, imports);
            var command = false; string? rename = null;
            for (var index = from; index < end; index++)
            {
                Charge();
                if (tokens[index].String) continue;
                if (tokens[index].Text == "#" && index + 1 < end && tokens[index + 1].Text == "[")
                {
                    var close = Close(tokens, index + 1);
                    var attribute = TokenText(tokens, index + 2, close - index - 2);
                    var commandName = tokens[index + 2].Text;
                    if (attribute.StartsWith("tauri::command", StringComparison.Ordinal) || imports.GetValueOrDefault(commandName) == "tauri::command")
                    {
                        command = true;
                        for (var cursor = index + 2; cursor + 2 < close; cursor++)
                        {
                            Charge();
                            if (tokens[cursor].Text == "rename" && tokens[cursor + 1].Text == "=" && tokens[cursor + 2].String) rename = tokens[cursor + 2].Text;
                        }
                    }
                    index = close; continue;
                }
                if (tokens[index].Text == "mod" && index + 2 < end && tokens[index + 2].Text == "{")
                {
                    var close = Close(tokens, index + 2);
                    Parse(tokens, index + 3, close, JoinModule(module, tokens[index + 1].Text), path, source, imports, definitions, registrations, constants, depth + 1);
                    index = close; command = false; rename = null; continue;
                }
                if (tokens[index].Text == "const" && index + 2 < end)
                {
                    var equals = index + 2; while (equals < end && tokens[equals].Text is not ("=" or ";" or "{")) { Charge(); equals++; }
                    if (equals < end && tokens[equals].Text == "=")
                    {
                        var semicolon = equals + 1; while (semicolon < end && tokens[semicolon].Text != ";") { Charge(); semicolon++; }
                        constants.Add(new Constant(module, tokens[index + 1].Text, TokenSlice(tokens, equals + 1, semicolon - equals - 1), imports));
                    }
                }
                if (tokens[index].Text == "fn" && index + 1 < end)
                {
                    var name = tokens[index + 1].Text;
                    var body = index + 2;
                    while (body < end && tokens[body].Text is not ("{" or ";")) { Charge(); body++; }
                    var close = body < end && tokens[body].Text == "{" ? Close(tokens, body) : body;
                    definitions.Add(new Definition(path, module, name, rename ?? name, Line(source, tokens[index + 1].Offset),
                        tokens[index].Offset, close < tokens.Count ? tokens[close].Offset : tokens[index].Offset, command, imports));
                    command = false; rename = null;
                }
                if (tokens[index].Text != "invoke_handler" || index < 1 || tokens[index - 1].Text != "." || index + 2 >= end || tokens[index + 1].Text != "(") continue;
                var handlerEnd = Close(tokens, index + 1);
                var start = index + 2;
                var macro = start;
                while (macro < handlerEnd && tokens[macro].Text != "!") { Charge(); macro++; }
                if (macro >= handlerEnd || macro + 1 >= handlerEnd || tokens[macro + 1].Text != "[")
                {
                    registrations.Add(new Registration(path, module, "<dynamic-handler>", null, Line(source, tokens[index].Offset), imports));
                    continue;
                }
                var macroPath = TokenText(tokens, start, macro - start);
                if (macroPath != "tauri::generate_handler" && imports.GetValueOrDefault(macroPath) != "tauri::generate_handler") continue;
                var listEnd = Close(tokens, macro + 1);
                string? plugin = null;
                var pluginMarker = FindToken(tokens, macro + 2, listEnd - macro - 2, "plugin");
                if (pluginMarker >= 0 && pluginMarker + 2 < listEnd) plugin = tokens[pluginMarker + 2].Text;
                // Inline plugin builders provide the runtime namespace even without the optional
                // generate_handler plugin annotation used by Tauri's permission optimizer.
                var statementStart = index - 1;
                while (statementStart > from && tokens[statementStart].Text is not (";" or "{" or "}")) { Charge(); statementStart--; }
                var prefix = TokenSlice(tokens, statementStart, index - statementStart);
                for (var cursor = 0; cursor + 6 < prefix.Count; cursor++)
                {
                    Charge();
                    if (prefix[cursor].Text == "plugin" && prefix[cursor + 2].Text == "Builder")
                        for (var inner = cursor + 3; inner + 2 < prefix.Count; inner++)
                        {
                            Charge();
                            if (prefix[inner].Text == "new" && prefix[inner + 1].Text == "(" && prefix[inner + 2].String) plugin = prefix[inner + 2].Text;
                        }
                }
                var item = macro + 2;
                if (pluginMarker >= 0) item = FindToken(tokens, pluginMarker, listEnd - pluginMarker + 1, "]") + 1;
                for (var cursor = item; cursor <= listEnd; cursor++)
                {
                    Charge();
                    if (cursor != listEnd && tokens[cursor].Text != ",") continue;
                    var target = TokenText(tokens, item, cursor - item);
                    if (target.Length > 0)
                        registrations.Add(new Registration(path, module, QualifiedPath(target) ? target : "<dynamic-command>", plugin, Line(source, tokens[index].Offset), imports));
                    item = cursor + 1;
                }
                index = handlerEnd;
            }
        }

        private Dictionary<string, string> Imports(List<Token> tokens, int from = 0, int? until = null, Dictionary<string, string>? inherited = null)
        {
            if (inherited is not null) foreach (var pair in inherited) Charge(pair.Key.Length + pair.Value.Length + 1);
            var result = inherited is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new(inherited, StringComparer.Ordinal);
            var limit = until ?? tokens.Count;
            for (var index = from; index < limit; index++)
            {
                Charge();
                if (tokens[index].String) continue;
                if (tokens[index].Text == "{") { index = Close(tokens, index); continue; }
                if (tokens[index].Text != "use") continue;
                var end = index + 1; while (end < limit && tokens[end].Text != ";") { Charge(); end++; }
                Expand(index + 1, end, "", 1); index = end;
            }
            return result;
            void Expand(int start, int end, string prefix, int nesting)
            {
                Depth(nesting);
                var open = FindToken(tokens, start, end - start, "{");
                if (open >= 0)
                {
                    Charge(prefix.Length);
                    var root = prefix + TokenText(tokens, start, open - start);
                    var close = Close(tokens, open); var item = open + 1; var depth = 0;
                    for (var cursor = item; cursor <= close; cursor++)
                    {
                        Charge();
                        if (tokens[cursor].String) continue;
                        if (tokens[cursor].Text == "{") depth++;
                        if (tokens[cursor].Text == "}") depth--;
                        if (cursor != close && (tokens[cursor].Text != "," || depth != 0)) continue;
                        if (cursor > item) Expand(item, cursor, root, nesting + 1); item = cursor + 1;
                    }
                }
                else
                {
                    var alias = FindToken(tokens, start, end - start, "as");
                    Charge(prefix.Length);
                    var value = prefix + TokenText(tokens, start, (alias < 0 ? end : alias) - start);
                    Charge(value.Length);
                    var name = alias >= 0 ? tokens[alias + 1].Text : value.Split("::")[^1];
                    if (name.Length > 0) result[name] = value;
                }
            }
        }

        private int Close(List<Token> tokens, int open)
        {
            Charge();
            return open >= 0 && open < tokens.Count && _delimiters.TryGetValue(tokens, out var matching) && matching[open] >= 0
                ? matching[open] : tokens.Count - 1;
        }

        private bool Balanced(List<Token> tokens)
        {
            Charge(tokens.Count);
            var stack = new Stack<int>();
            var matching = new int[tokens.Count]; Array.Fill(matching, -1);
            for (var index = 0; index < tokens.Count; index++)
            {
                Charge();
                var token = tokens[index]; if (token.String) continue;
                if (token.Text is "(" or "[" or "{") { Depth(stack.Count + 1); stack.Push(index); }
                else if (token.Text is ")" or "]" or "}")
                {
                    if (stack.Count == 0) return false;
                    var open = stack.Pop();
                    if ((tokens[open].Text, token.Text) is not (("(", ")") or ("[", "]") or ("{", "}"))) return false;
                    matching[open] = index; matching[index] = open;
                }
            }
            if (stack.Count == 0) _delimiters[tokens] = matching;
            return stack.Count == 0;
        }

        private List<Token> Lex(string source, out string? error)
        {
            var tokens = new List<Token>();
            error = null;
            for (var index = 0; index < source.Length;)
            {
                Charge();
                var start = index;
                if (char.IsWhiteSpace(source[index])) { index++; continue; }
                if (index + 1 < source.Length && source[index..(index + 2)] == "//")
                { while (index < source.Length && source[index] != '\n') { Charge(); index++; } continue; }
                if (index + 1 < source.Length && source[index..(index + 2)] == "/*")
                {
                    index += 2; var depth = 1;
                    while (index + 1 < source.Length && depth > 0)
                    {
                        Charge();
                        if (source[index..(index + 2)] == "/*") { depth++; index += 2; }
                        else if (source[index..(index + 2)] == "*/") { depth--; index += 2; }
                        else index++;
                    }
                    if (depth > 0) tokens.Add(new Token("{", start));
                    continue;
                }
                var raw = index;
                if (source[index] is 'b' or 'c' && index + 1 < source.Length && source[index + 1] == 'r') raw++;
                var rawPrefix = raw;
                if (source[raw] == 'r')
                {
                    raw++; while (raw < source.Length && source[raw] == '#') { Charge(); raw++; }
                    if (raw < source.Length && source[raw] == '"')
                    {
                        Charge(raw - rawPrefix);
                        var closing = "\"" + new string('#', raw - rawPrefix - 1);
                        var end = FindText(source, closing, raw + 1);
                        if (end < 0) { tokens.Add(new Token("{", start)); break; }
                        Charge(end - raw);
                        tokens.Add(new Token(source[(raw + 1)..end], start, true)); index = end + closing.Length; continue;
                    }
                }
                if (source[index] == '"')
                {
                    index++;
                    while (index < source.Length && source[index] != '"') { Charge(); if (source[index] == '\\') index++; index++; }
                    if (index >= source.Length) { tokens.Add(new Token("{", start)); break; }
                    index++;
                    Charge(index - start);
                    try { tokens.Add(new Token(JsonSerializer.Deserialize<string>(source[start..index])!, start, true)); }
                    catch (JsonException) { tokens.Add(new Token("<unsupported-string>", start, true)); }
                    continue;
                }
                if (source[index] == '\'')
                {
                    var isByte = tokens.Count > 0 && tokens[^1].Text == "b" && tokens[^1].Offset == index - 1;
                    if (!TryCharacterLiteral(source, index, isByte, out var end, out var lifetime))
                    { error = $"malformed Rust character literal at line {Line(source, start)}"; break; }
                    if (!lifetime)
                    { Charge(end - start); tokens.Add(new Token(source[start..end], start, true)); index = end; continue; }
                    // Keep lifetime/label tokenization unchanged: apostrophe followed by identifier.
                }
                if (char.IsLetterOrDigit(source[index]) || source[index] == '_')
                {
                    index++; while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) { Charge(); index++; }
                    Charge(index - start);
                    tokens.Add(new Token(source[start..index], start)); continue;
                }
                if (index + 1 < source.Length && source[index..(index + 2)] == "::")
                { tokens.Add(new Token("::", start)); index += 2; continue; }
                tokens.Add(new Token(source[index++].ToString(), start));
            }
            return tokens;
        }

        private bool TryCharacterLiteral(string source, int start, bool isByte, out int end, out bool lifetime)
        {
            Charge();
            var cursor = start + 1;
            end = cursor; lifetime = false;
            if (cursor >= source.Length) return false;
            if (source[cursor] == '\\')
            {
                if (++cursor >= source.Length) return false;
                var escape = source[cursor++];
                if (escape == 'x')
                {
                    if (cursor + 1 >= source.Length || !char.IsAsciiHexDigit(source[cursor]) || !char.IsAsciiHexDigit(source[cursor + 1])) return false;
                    // Character hex escapes are ASCII; byte literals also permit 0x80..0xff.
                    if (!isByte && source[cursor] > '7') return false;
                    cursor += 2;
                }
                else if (escape == 'u')
                {
                    if (isByte || cursor >= source.Length || source[cursor++] != '{') return false;
                    var digits = 0; var value = 0;
                    while (cursor < source.Length && source[cursor] != '}')
                    {
                        Charge();
                        var character = source[cursor++];
                        if (character == '_' && digits > 0) continue;
                        if (!char.IsAsciiHexDigit(character) || ++digits > 6) return false;
                        value = value * 16 + (character <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10);
                    }
                    if (digits == 0 || cursor >= source.Length || !System.Text.Rune.IsValid(value)) return false;
                    cursor++;
                }
                else if (escape is not ('n' or 'r' or 't' or '0' or '\\' or '\'' or '"')) return false;
            }
            else
            {
                if (!System.Text.Rune.TryGetRuneAt(source, cursor, out var scalar) || scalar.Value is '\'' or '\n' or '\r' or '\t'
                    || isByte && !scalar.IsAscii) return false;
                cursor += scalar.Utf16SequenceLength;
                if (!isByte && (System.Text.Rune.IsLetter(scalar) || scalar.Value == '_') && (cursor >= source.Length || source[cursor] != '\''))
                {
                    // A named lifetime/loop label has no closing quote. A closed multi-character
                    // token such as 'ab' is malformed, rather than a lifetime plus stray apostrophe.
                    while (cursor < source.Length && (char.IsLetterOrDigit(source[cursor]) || source[cursor] == '_'
                        || char.GetUnicodeCategory(source[cursor]) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)) { Charge(); cursor++; }
                    if (cursor < source.Length && source[cursor] == '\'') return false;
                    lifetime = true; return true;
                }
            }
            if (cursor >= source.Length || source[cursor] != '\'') return false;
            end = cursor + 1; return true;
        }
    }
}
