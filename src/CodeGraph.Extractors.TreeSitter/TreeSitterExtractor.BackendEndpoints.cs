using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

public partial class TreeSitterExtractor
{
    public bool SupportsBackendEndpoints(string extension) => extension is ".rs" or ".go";

    public bool MayContainBackendEndpoints(string extension, string content) =>
        SupportsBackendEndpoints(extension) && Regex.IsMatch(content,
            extension == ".rs" ? @"\.\s*(route|nest|merge)\s*\(" : @"\.\s*(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|CONNECT|TRACE|Any|Handle)\s*\(");

    public Task<ExtractionResult> ExtractBackendEndpointsAsync(string filePath, string content,
        ExtractorContext context, IReadOnlyCollection<GraphNode> availableNodes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!SupportsBackendEndpoints(Path.GetExtension(filePath)))
            return Task.FromResult(new ExtractionResult());
        try
        {
            var work = context.BackendEndpointWork ?? new BackendEndpointWorkBudget();
            work.Charge(content.Length, ct); // charge before native UTF conversion/parse
            var handlers = availableNodes as BackendEndpointHandlerIndex
                ?? new BackendEndpointHandlerIndex(availableNodes, context.RepositoryName, work, ct);
            using var parser = new Parser { Language = _specForExtension(Path.GetExtension(filePath))!.GetLanguage() };
            using var tree = parser.Parse(content);
            // Registration extraction cannot safely replace a previously complete route tree from
            // a recovered syntax tree: partial output could silently remove endpoints.
            if (tree is null || tree.RootNode.HasError)
                return Task.FromResult(ExtractionResult.Failure("Backend registration source has syntax errors"));
            var pass = new BackendRegistrationPass(tree.RootNode, filePath, content, context, handlers, work, ct);
            return Task.FromResult(pass.Extract());
        }
        catch (BackendEndpointLimitException ex)
        {
            return Task.FromResult(new ExtractionResult { Succeeded = false, Complete = false, FailureReason = ex.Message });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ExtractionResult.Failure($"Backend registration extraction failed: {ex.Message}"));
        }
    }

    private sealed class BackendRegistrationPass(Node root, string filePath, string content, ExtractorContext context,
        BackendEndpointHandlerIndex handlers, BackendEndpointWorkBudget work, CancellationToken ct)
    {
        private readonly string _relative = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
        private Dictionary<string, string> _imports = new(StringComparer.Ordinal);
        private readonly List<GraphNode> _nodes = [];
        private readonly List<PendingEdge> _edges = [];
        private readonly List<Router> _routers = [];
        private readonly bool _rust = Path.GetExtension(filePath) == ".rs";
        private List<Node> _functions = [];
        private List<Node> _rustUses = [];
        private readonly Dictionary<(int Start, int End, string Type), (string Scope, Node? Function)> _locations = [];
        private readonly Dictionary<(int Start, int End, string Type), string> _texts = [];
        private readonly Dictionary<(int Start, int End, string Type), string?> _literals = [];
        private readonly Dictionary<(string Scope, string Name), HashSet<int>> _functionLines = [];
        private readonly Dictionary<((int, int, string) Function, string Name), int> _shadowStarts = [];
        private readonly Dictionary<string, Dictionary<string, string>> _scopeImports = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Expression, string Scope), IReadOnlyList<GraphNode>> _resolved = [];
        private readonly List<Node> _goImports = [];
        private readonly Dictionary<string, string> _emptyImports = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ginAliases = new(StringComparer.Ordinal);
        private int _evaluationDepth;
        private string? _goModule;
        private string? _goRoot;

        // Keep this expansion aligned with the matcher's concrete HTTP methods. A wildcard
        // registration must use those ordinary route identities to remain matchable.
        private static readonly string[] WildcardHttpMethods =
            ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
        private sealed record Registration(string? Path, string Verb, Node Handler, Node Origin, bool Wildcard = false, string? RegistrationMethod = null);
        private sealed class Router
        {
            public List<Registration> Routes { get; } = [];
            public bool Consumed { get; set; }
            public string? Prefix { get; init; } = "";
        }

        public ExtractionResult Extract()
        {
            IndexAst();
            ReadImports();
            work.Charge(content.Length, ct);
            if (_rust ? !_scopeImports.Values.Any(map => map.Values.Any(x => x.StartsWith("axum::", StringComparison.Ordinal))) && !content.Contains("axum::Router")
                      : !_imports.Values.Contains("github.com/gin-gonic/gin"))
                return new ExtractionResult();
            foreach (var function in _functions)
            {
                ct.ThrowIfCancellationRequested();
                if (_rust) SetRustImports(function);
                var env = new Dictionary<string, Router>(StringComparer.Ordinal);
                if (!_rust)
                {
                    foreach (var parameter in Descendants(function.GetChildForField("parameters"))
                                 .Where(n => n.Type == "parameter_declaration"))
                    {
                        var type = Text(parameter.GetChildForField("type"));
                        if (type.StartsWith('*') && type.LastIndexOf('.') is var dot && dot > 1
                            && _ginAliases.Contains(type[1..dot]) && type[(dot + 1)..] is "Engine" or "RouterGroup")
                            foreach (var name in parameter.NamedChildren.Where(n => n.Type == "identifier"))
                                env[Text(name)] = new Router { Prefix = type.EndsWith("RouterGroup") ? null : "" };
                    }
                }
                Process(function.GetChildForField("body"), env);
            }
            foreach (var router in _routers.Where(r => !r.Consumed))
            {
                // Axum's any handler is the MethodRouter fallback: an explicit method wins,
                // including when Router::route merges registrations for the same static path.
                // Gin registers every Any method directly, so conflicting entries remain ambiguous.
                foreach (var route in router.Routes) work.Charge(6L * (1 + (route.Path?.Length ?? 0)), ct);
                var registrations = router.Routes;
                if (_rust)
                {
                    // Axum tries explicit HEAD, then GET, before its any fallback. Keep the
                    // wildcard expansion from incorrectly attributing HEAD to that fallback.
                    var wildcardHeadPaths = registrations.Where(r => r.Wildcard && r.Verb == "HEAD" && r.Path is not null)
                        .Select(r => r.Path).ToHashSet();
                    var explicitHeadPaths = registrations.Where(r => !r.Wildcard && r.Verb == "HEAD")
                        .Select(r => r.Path).ToHashSet();
                    registrations = registrations.Concat(registrations.Where(r => !r.Wildcard && r.Verb == "GET"
                            && wildcardHeadPaths.Contains(r.Path) && !explicitHeadPaths.Contains(r.Path))
                        .Select(r => r with { Verb = "HEAD", RegistrationMethod = "GET" })).ToList();
                }
                var explicitMethods = registrations.Where(r => !r.Wildcard && r.Path is not null)
                    .Select(r => (r.Path, r.Verb)).ToHashSet();
                foreach (var registration in registrations)
                    if (!_rust || !registration.Wildcard || !explicitMethods.Contains((registration.Path, registration.Verb)))
                        Emit(registration);
            }
            foreach (var node in _nodes) work.Charge(node.QualifiedName.Length * 3L +
                (node.Properties.GetValueOrDefault("handler_qualified_name")?.ToString()?.Length ?? 0) * 2L, ct);
            foreach (var edge in _edges) work.Charge(edge.SourceQN.Length * 2L + edge.TargetQN.Length, ct);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var duplicate in _nodes.GroupBy(n => n.QualifiedName).Where(g => g.Select(n =>
                         n.Properties.GetValueOrDefault("handler_qualified_name")?.ToString() ?? n.Properties["handler_expression"].ToString()).Distinct().Count() > 1))
            {
                foreach (var route in duplicate)
                {
                    route.Properties["resolution_status"] = "ambiguous_handler";
                    route.Properties["registration_ambiguous"] = true;
                    route.Properties["confidence"] = "low";
                    route.Properties.Remove("handler_qualified_name");
                }
                ambiguous.Add(duplicate.Key);
            }
            _edges.RemoveAll(edge => ambiguous.Contains(edge.SourceQN));
            return new ExtractionResult { Nodes = _nodes.DistinctBy(n => n.QualifiedName).ToList(),
                Edges = _edges.DistinctBy(e => (e.SourceQN, e.TargetQN, e.Type)).ToList() };
        }

        private void ReadImports()
        {
            if (_rust)
            {
                foreach (var group in _rustUses.GroupBy(RustScope))
                {
                    _imports = new(StringComparer.Ordinal);
                    foreach (var node in group) { work.Charge(1, ct); ReadRustUse(node); }
                    _scopeImports[group.Key] = _imports;
                }
            }
            else foreach (var node in _goImports)
            {
                work.Charge(1, ct);
                var path = Literal(node.GetChildForField("path"));
                if (path is not null)
                    _imports[node.GetChildForField("name") is { } alias ? Text(alias) : path.Split('/').Last()] = path;
            }
            if (!_rust)
            {
                foreach (var import in _imports) if (import.Value == "github.com/gin-gonic/gin") _ginAliases.Add(import.Key);
                var directory = DirectoryOf(_relative);
                while (true)
                {
                    var manifest = directory.Length == 0 ? "go.mod" : directory + "/go.mod";
                    if (context.GoModuleManifests.TryGetValue(manifest, out var content))
                    {
                        work.Charge(content.Length, ct);
                        var modules = Regex.Matches(content, @"(?m)^[ \t]*module[ \t]+(?:""(?<path>[^""\r\n]+)""|(?<path>[^ \t\r\n""]+))[ \t]*(?://[^\r\n]*)?\r?$");
                        if (modules.Count != 1)
                            throw new InvalidOperationException($"Supplied Go manifest {manifest} has no unique module identity");
                        _goModule = modules[0].Groups["path"].Value;
                        _goRoot = directory;
                        break;
                    }
                    if (directory.Length == 0) break;
                    directory = DirectoryOf(directory);
                }
            }
        }

        private void SetRustImports(Node origin)
        {
            work.Charge(1, ct);
            _imports = _scopeImports.GetValueOrDefault(RustScope(origin)) ?? _emptyImports;
        }

        private void ReadRustUse(Node node)
        {
            var text = Text(node).Trim().TrimEnd(';');
            text = text[(text.IndexOf("use ", StringComparison.Ordinal) + 4)..].Trim();
            ExpandUse(text, "");
        }

        private void ExpandUse(string text, string prefix, int depth = 0)
        {
            work.CheckDepth(depth, ct);
            if (depth > 512) throw new BackendEndpointLimitException("Backend endpoint evaluation depth limit exceeded");
            work.Charge(text.Length + prefix.Length, ct);
            var brace = text.IndexOf('{');
            if (brace >= 0 && text.EndsWith('}'))
            {
                var nesting = 0; var start = brace + 1;
                for (var i = start; i < text.Length; i++)
                {
                    if (text[i] == '{') nesting++;
                    if ((text[i] == ',' && nesting == 0) || i == text.Length - 1)
                    { ExpandUse(text[start..i].Trim(), prefix + text[..brace], depth + 1); start = i + 1; }
                    if (text[i] == '}') nesting--;
                }
                return;
            }
            var parts = Regex.Split(text, @"\s+as\s+");
            var target = prefix + parts[0].Trim();
            if (target.EndsWith("::self")) target = target[..^6];
            _imports[parts.Length == 2 ? parts[1].Trim() : target.Split("::").Last()] = target;
        }

        private void Process(Node? node, Dictionary<string, Router> env)
        {
            if (node is null) return;
            using var evaluation = EnterEvaluation();
            work.Charge(1, ct);
            if (node.Type is "let_declaration" or "short_var_declaration" or "var_spec" or "assignment_expression" or "assignment_statement")
            {
                var value = node.GetChildForField("value") ?? node.GetChildForField("right");
                if (value?.Type == "expression_list") value = value.NamedChildren.FirstOrDefault();
                var name = node.GetChildForField("pattern") ?? node.GetChildForField("left") ?? node.GetChildForField("name");
                if (name?.Type == "expression_list") name = name.NamedChildren.FirstOrDefault();
                if (value is not null && name is not null)
                {
                    var router = Evaluate(value, env);
                    if (router is not null) env[Text(name)] = router;
                    else env.Remove(Text(name));
                }
                return;
            }
            if (node.Type == "call_expression") { Evaluate(node, env); return; }
            if (node.Type is "function_item" or "function_declaration" or "method_declaration" or "closure_expression" or "func_literal") return;
            foreach (var child in node.NamedChildren)
                Process(child, node.Type is "if_expression" or "if_statement" ? CopyEnvironment(env) : env);
        }

        private Router? Evaluate(Node node, Dictionary<string, Router> env)
        {
            using var evaluation = EnterEvaluation();
            work.Charge(1, ct);
            if (node.Type == "identifier") return env.GetValueOrDefault(Text(node));
            if (node.Type is "reference_expression" or "parenthesized_expression" or "try_expression")
                return node.NamedChildren.Select(n => Evaluate(n, env)).FirstOrDefault(r => r is not null);
            if (node.Type != "call_expression") return null;
            var function = node.GetChildForField("function");
            if (function is null) return null;
            var arguments = node.GetChildForField("arguments")?.NamedChildren.ToList() ?? [];
            var receiver = function.GetChildForField(_rust ? "value" : "operand");
            var field = function.GetChildForField("field");
            var method = field is null ? null : Text(field);
            var constructor = false;
            if (_rust && receiver is null)
            {
                var text = Text(function);
                constructor = text.EndsWith("::new") && ExpandPath(text[..^5]) == "axum::Router";
            }
            else if (!_rust && receiver?.Type == "identifier" && method is "New" or "Default")
                constructor = _imports.GetValueOrDefault(Text(receiver)) == "github.com/gin-gonic/gin";
            if (constructor) return NewRouter();
            if (receiver is null || method is null) return null;
            var parent = Evaluate(receiver, env);
            if (parent is null) return null;
            if (!_rust)
            {
                if (method == "Group" && arguments.Count > 0)
                    return new Router { Prefix = Join(parent.Prefix, Literal(arguments[0])) };
                if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" or "CONNECT" or "TRACE" or "Any" or "Handle")
                {
                    var offset = method == "Handle" ? 1 : 0;
                    if (arguments.Count < offset + 2) return parent;
                    var verb = method == "Handle" ? Literal(arguments[0])?.ToUpperInvariant() ?? "UNKNOWN" : method == "Any" ? "ANY" : method;
                    var registration = new Registration(Join(parent.Prefix, Literal(arguments[offset])), verb, arguments[^1], function.GetChildForField("field") ?? node, Wildcard: method == "Any");
                    var output = NewRouter();
                    AddRegistration(output, registration);
                }
                return parent;
            }
            // Rust routers are values; transfer the accumulated tree to the outer builder.
            var result = NewRouter(); parent.Consumed = true;
            work.Charge(parent.Routes.Count, ct);
            result.Routes.AddRange(parent.Routes);
            if (method == "route" && arguments.Count >= 2)
            {
                foreach (var (verb, handler) in MethodHandlers(arguments[1]))
                    AddRegistration(result, new Registration(Literal(arguments[0]), verb, handler, function.GetChildForField("field") ?? node, Wildcard: verb == "ANY"));
            }
            else if (method is "nest" or "merge" && arguments.Count >= (method == "nest" ? 2 : 1))
            {
                var nested = Evaluate(arguments[method == "nest" ? 1 : 0], env);
                if (nested is not null)
                {
                    nested.Consumed = true;
                    work.Charge(nested.Routes.Count, ct);
                    result.Routes.AddRange(nested.Routes.Select(r => r with { Path = method == "nest" ? Join(Literal(arguments[0]), r.Path) : r.Path }));
                }
            }
            return result;
        }

        private void AddRegistration(Router router, Registration registration)
        {
            work.Charge(registration.Wildcard ? WildcardHttpMethods.Length : 1, ct);
            if (registration.Wildcard)
                foreach (var method in WildcardHttpMethods)
                    router.Routes.Add(registration with { Verb = method, Wildcard = true, RegistrationMethod = "ANY" });
            else router.Routes.Add(registration);
        }

        private Router NewRouter() { var result = new Router(); _routers.Add(result); return result; }
        private string ExpandPath(string path)
        {
            var parts = path.Split("::", 2);
            return _imports.TryGetValue(parts[0], out var imported) ? imported + (parts.Length > 1 ? "::" + parts[1] : "") : path;
        }
        private IEnumerable<(string Verb, Node Handler)> MethodHandlers(Node node)
        {
            using var evaluation = EnterEvaluation();
            work.Charge(1, ct);
            if (node.Type != "call_expression") yield break;
            var function = node.GetChildForField("function");
            var args = node.GetChildForField("arguments")?.NamedChildren.ToList() ?? [];
            if (function is null || args.Count == 0) yield break;
            var receiver = function.GetChildForField("value");
            var name = function.GetChildForField("field") is { } field ? Text(field) : ExpandPath(Text(function)).Split("::").Last();
            if (receiver is not null)
                foreach (var previous in MethodHandlers(receiver)) yield return previous;
            else if (!ExpandPath(Text(function)).StartsWith("axum::routing::", StringComparison.Ordinal)) yield break;
            if (name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace" or "connect" or "any")
                yield return (name.ToUpperInvariant(), args[0]);
        }

        private void Emit(Registration registration)
        {
            work.Charge(1, ct);
            if (_rust) SetRustImports(registration.Origin);
            var path = registration.Path;
            var handlerText = registration.Handler.Type == "call_expression"
                ? Text(registration.Handler.GetChildForField("function") ?? registration.Handler)
                : Text(registration.Handler);
            var targets = ResolveHandler(handlerText, registration.Origin);
            var resolved = path is not null && registration.Verb != "UNKNOWN" && targets.Count == 1;
            work.Charge((path?.Length ?? 0) * 4L + _relative.Length * 3L + handlerText.Length * 5L
                + context.RepositoryName.Length + (targets.Count == 1 ? targets[0].QualifiedName.Length : 0), ct);
            var routeIdentity = path ?? $"dynamic:{_relative}:{registration.Origin.StartPosition.Row + 1}:{registration.Origin.StartPosition.Column + 1}";
            var qn = $"route:{context.RepositoryName}:{registration.Verb}:{routeIdentity}";
            var properties = new Dictionary<string, object>
            {
                ["http_method"] = registration.Verb, ["route_kind"] = "endpoint",
                ["framework"] = _rust ? "axum" : "gin", ["source"] = "tree-sitter-registration",
                ["origin_file"] = _relative, ["handler_expression"] = handlerText,
                ["handler_name"] = Regex.IsMatch(handlerText, _rust ? @"^\w+(::\w+)*$" : @"^\w+(\.\w+)*$")
                    ? (_rust ? ExpandPath(handlerText) : handlerText).Split(_rust ? "::" : ".").Last() : "",
                ["semantic_owner"] = "http_endpoints",
                ["handler_scope"] = _rust ? ExpandPath(handlerText) : handlerText.Contains('.') && _imports.TryGetValue(handlerText.Split('.')[0], out var package) ? package : DirectoryOf(_relative),
                ["resolution_status"] = resolved ? "resolved" : path is null ? "dynamic_path" : registration.Verb == "UNKNOWN" ? "dynamic_method" : targets.Count > 1 ? "ambiguous_handler" : "unresolved_handler",
                ["confidence"] = resolved ? "high" : "low"
            };
            if (registration.RegistrationMethod is not null) properties["registration_method"] = registration.RegistrationMethod;
            if (path is not null) properties["route_template"] = path;
            if (resolved) properties["handler_qualified_name"] = targets[0].QualifiedName;
            _nodes.Add(new GraphNode { Repository = context.RepositoryName, Label = NodeLabel.Route,
                Name = $"{registration.Verb} {path ?? "<dynamic>"}", QualifiedName = qn, FilePath = _relative,
                StartLine = (int)registration.Origin.StartPosition.Row + 1, EndLine = (int)registration.Origin.EndPosition.Row + 1, Properties = properties });
            if (resolved) _edges.Add(new(qn, targets[0].QualifiedName, EdgeType.HANDLES,
                new() { ["origin_file"] = _relative, ["semantic_owner"] = "http_endpoints", ["framework"] = _rust ? "axum" : "gin", ["confidence"] = "high" }));
        }

        private IReadOnlyList<GraphNode> ResolveHandler(string expression, Node origin)
        {
            work.Charge(expression.Length + 1, ct);
            if (!Regex.IsMatch(expression, _rust ? @"^\w+(::\w+)*$" : @"^\w+(\.\w+)*$")) return [];
            var separator = _rust ? "::" : ".";
            var parts = expression.Split(separator);
            var location = _locations[Key(origin)];
            work.Charge(parts[0].Length, ct);
            if (location.Function is { } function && _shadowStarts.TryGetValue((Key(function), parts[0]), out var shadow)
                && shadow < origin.StartIndex) return [];
            work.Charge(expression.Length + location.Scope.Length, ct);
            var cacheKey = (expression, location.Scope);
            if (_resolved.TryGetValue(cacheKey, out var cached)) return cached;
            var name = (_rust ? ExpandPath(expression).Split("::") : parts)[^1];
            var result = new List<GraphNode>(2);
            void Candidates(IReadOnlyList<GraphNode> candidates, HashSet<int>? lines = null)
            {
                if (lines is { Count: 0 }) return;
                foreach (var candidate in candidates)
                {
                    work.Charge(1L + candidate.QualifiedName.Length * (result.Count + 1), ct);
                    if ((lines is null || lines.Contains(candidate.StartLine)) && result.All(n => n.QualifiedName != candidate.QualifiedName)) result.Add(candidate);
                    if (result.Count == 2) break;
                }
            }
            void File(string path, HashSet<int>? lines = null)
            { work.Charge(path.Length + name.Length, ct); if (result.Count < 2) Candidates(handlers.InFile(_rust ? ".rs" : ".go", name, path), lines); }
            if (!_rust)
            {
                if (parts.Length == 1)
                { work.Charge(name.Length + _relative.Length, ct); Candidates(handlers.InDirectory(".go", name, DirectoryOf(_relative))); }
                else if (parts.Length == 2 && _imports.TryGetValue(parts[0], out var import) && _goModule is not null && _goRoot is not null
                    && (import == _goModule || import.StartsWith(_goModule + "/", StringComparison.Ordinal)))
                {
                    var directory = string.Join('/', new[] { _goRoot == "." ? "" : _goRoot, import[_goModule.Length..].Trim('/') }.Where(x => x.Length > 0));
                    work.Charge(name.Length + directory.Length, ct);
                    Candidates(handlers.InDirectory(".go", name, directory));
                }
            }
            else
            {
                parts = ExpandPath(expression).Split("::");
                if (parts.Length == 1)
                { work.Charge(location.Scope.Length + name.Length, ct); File(_relative, _functionLines.GetValueOrDefault((location.Scope, name)) ?? []); }
                else
                {
                    var srcIndex = _relative.LastIndexOf("/src/", StringComparison.Ordinal);
                    var crate = srcIndex >= 0 ? _relative[..(srcIndex + 5)] : _relative.StartsWith("src/") ? "src/" : (DirectoryOf(_relative).Length == 0 ? "" : DirectoryOf(_relative) + "/");
                    var module = Regex.Replace(_relative[crate.Length..].Replace("/", "::"), @"(^|::)(lib|main|mod)\.rs$|\.rs$", "").Trim(':');
                    var fileModules = module.Length == 0 ? [] : module.Split("::");
                    var segments = fileModules.ToList();
                    if (location.Scope.Length > 0) segments.AddRange(location.Scope.Split("::"));
                    if (_imports.ContainsKey(expression.Split("::")[0]) && parts[0] is not ("self" or "super" or "crate")) segments.Clear();
                    work.Charge(parts.Length + location.Scope.Length + _relative.Length, ct);
                    var start = 0;
                    if (parts[0] == "crate") { segments.Clear(); start++; }
                    else if (parts[0] == "self") start++;
                    while (start < parts.Length - 1 && parts[start] == "super")
                    { work.Charge(1, ct); if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); start++; }
                    segments.AddRange(parts.Skip(start).Take(parts.Length - start - 1));
                    var target = crate + string.Join('/', segments);
                    var inlineScope = string.Join("::", segments.Skip(fileModules.Length));
                    work.Charge(inlineScope.Length + name.Length, ct);
                    if (segments.Take(fileModules.Length).SequenceEqual(fileModules))
                        File(_relative, _functionLines.GetValueOrDefault((inlineScope, name)) ?? []);
                    File(target + ".rs"); File(target + "/mod.rs");
                    if (segments.Count == 0) { File(crate + "lib.rs"); File(crate + "main.rs"); }
                }
            }
            return _resolved[cacheKey] = result;
        }

        private Dictionary<string, Router> CopyEnvironment(Dictionary<string, Router> env)
        { work.Charge(env.Count, ct); return new(env, StringComparer.Ordinal); }
        private EvaluationScope EnterEvaluation()
        {
            work.Charge(1, ct);
            if (_evaluationDepth >= 512) throw new BackendEndpointLimitException("Backend endpoint evaluation depth limit exceeded");
            _evaluationDepth++; return new(this);
        }
        private readonly struct EvaluationScope(BackendRegistrationPass pass) : IDisposable
        { public void Dispose() => pass._evaluationDepth--; }
        private static (int, int, string) Key(Node node) => (node.StartIndex, node.EndIndex, node.Type);
        private string RustScope(Node node)
        { var scope = _locations[Key(node)].Scope; work.Charge(scope.Length + 1, ct); return scope; }
        private string Text(Node? node)
        {
            if (node is null) return "";
            var key = Key(node);
            if (_texts.TryGetValue(key, out var cached)) return cached;
            work.Charge(Math.Max(1, (long)node.EndIndex - node.StartIndex), ct);
            return _texts[key] = node.Text;
        }
        private void IndexAst()
        {
            var pending = new Stack<(Node Node, int Depth, string Scope, Node? Function)>();
            pending.Push((root, 0, "", null));
            while (pending.TryPop(out var item))
            {
                work.CheckDepth(item.Depth, ct);
                var node = item.Node;
                _locations[Key(node)] = (item.Scope, item.Function);
                var function = item.Function;
                if (node.Type is "function_item" or "function_declaration" or "method_declaration")
                {
                    _functions.Add(node); function = node;
                    var name = Text(node.GetChildForField("name"));
                    work.Charge(item.Scope.Length + name.Length, ct);
                    if (!_functionLines.TryGetValue((item.Scope, name), out var lines)) _functionLines[(item.Scope, name)] = lines = [];
                    lines.Add(node.StartPosition.Row + 1);
                    if (node.GetChildForField("name") is { } nameNode) lines.Add(nameNode.StartPosition.Row + 1);
                }
                if (node.Type == "use_declaration") _rustUses.Add(node);
                if (node.Type == "import_spec") _goImports.Add(node);
                if (function is not null && node.Type is "let_declaration" or "short_var_declaration" or "parameter" or "parameter_declaration")
                {
                    var name = Text(node.GetChildForField("pattern") ?? node.GetChildForField("name") ?? node.GetChildForField("left"));
                    work.Charge(name.Length, ct);
                    var key = (Key(function), name);
                    _shadowStarts[key] = Math.Min(_shadowStarts.GetValueOrDefault(key, int.MaxValue), node.StartIndex);
                }
                var scope = item.Scope;
                if (node.Type == "mod_item")
                { var name = Text(node.GetChildForField("name")); work.Charge(scope.Length + name.Length, ct); scope = scope.Length == 0 ? name : scope + "::" + name; }
                foreach (var child in node.NamedChildren)
                { work.Charge(1, ct); pending.Push((child, item.Depth + 1, scope, function)); }
            }
        }
        private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        private string? Join(string? prefix, string? path)
        {
            if (prefix is null || path is null) return null;
            work.Charge(prefix.Length + path.Length, ct);
            var joined = prefix.TrimEnd('/') + "/" + path.TrimStart('/');
            return "/" + joined.TrimStart('/');
        }
        private string? Literal(Node? node)
        {
            if (node is null || node.Type is not ("string_literal" or "raw_string_literal" or "interpreted_string_literal")) return null;
            var key = Key(node);
            if (_literals.TryGetValue(key, out var cached)) return cached;
            var text = Text(node);
            work.Charge(text.Length, ct);
            string? value = null;
            if (text.StartsWith('`') && text.EndsWith('`')) value = text[1..^1];
            else if (text.StartsWith('r'))
            { var first = text.IndexOf('"'); var last = text.LastIndexOf('"'); value = first >= 0 && last > first ? text[(first + 1)..last] : null; }
            else try { value = JsonSerializer.Deserialize<string>(text); } catch (JsonException) { }
            return _literals[key] = value;
        }
        private IEnumerable<Node> Descendants(Node? node)
        {
            if (node is null) yield break;
            var pending = new Stack<Node>(); pending.Push(node);
            while (pending.TryPop(out var current))
            {
                work.Charge(1, ct); yield return current;
                foreach (var child in current.NamedChildren) { work.Charge(1, ct); pending.Push(child); }
            }
        }
    }
}
