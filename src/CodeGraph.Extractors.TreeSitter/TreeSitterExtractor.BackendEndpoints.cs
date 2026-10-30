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
            using var parser = new Parser { Language = _specForExtension(Path.GetExtension(filePath))!.GetLanguage() };
            using var tree = parser.Parse(content);
            // Registration extraction cannot safely replace a previously complete route tree from
            // a recovered syntax tree: partial output could silently remove endpoints.
            if (tree is null || tree.RootNode.HasError)
                return Task.FromResult(ExtractionResult.Failure("Backend registration source has syntax errors"));
            var pass = new BackendRegistrationPass(tree.RootNode, filePath, context, availableNodes, ct);
            return Task.FromResult(pass.Extract());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ExtractionResult.Failure($"Backend registration extraction failed: {ex.Message}"));
        }
    }

    private sealed class BackendRegistrationPass(Node root, string filePath, ExtractorContext context,
        IReadOnlyCollection<GraphNode> availableNodes, CancellationToken ct)
    {
        private readonly string _relative = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
        private readonly Dictionary<string, string> _imports = new(StringComparer.Ordinal);
        private readonly List<GraphNode> _nodes = [];
        private readonly List<PendingEdge> _edges = [];
        private readonly List<Router> _routers = [];
        private readonly bool _rust = Path.GetExtension(filePath) == ".rs";
        private List<Node> _rustModules = [];
        private List<Node> _functions = [];
        private List<Node> _rustUses = [];
        private readonly Dictionary<(int, int), List<Node>> _shadowDeclarations = [];
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
            _functions = Descendants(root).Where(n => n.Type is "function_item" or "function_declaration" or "method_declaration").ToList();
            if (_rust)
            {
                _rustModules = Descendants(root).Where(n => n.Type == "mod_item").ToList();
                _rustUses = Descendants(root).Where(n => n.Type == "use_declaration").ToList();
            }
            ReadImports();
            if (_rust ? !_imports.Values.Any(x => x.StartsWith("axum::", StringComparison.Ordinal)) && !root.Text.Contains("axum::Router")
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
                        var type = parameter.GetChildForField("type")?.Text ?? "";
                        if (_imports.Any(i => i.Value == "github.com/gin-gonic/gin" &&
                                              (type == $"*{i.Key}.Engine" || type == $"*{i.Key}.RouterGroup")))
                            foreach (var name in parameter.NamedChildren.Where(n => n.Type == "identifier"))
                                env[name.Text] = new Router { Prefix = type.EndsWith("RouterGroup") ? null : "" };
                    }
                }
                Process(function.GetChildForField("body"), env);
            }
            foreach (var router in _routers.Where(r => !r.Consumed))
            {
                // Axum's any handler is the MethodRouter fallback: an explicit method wins,
                // including when Router::route merges registrations for the same static path.
                // Gin registers every Any method directly, so conflicting entries remain ambiguous.
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
                _edges.RemoveAll(e => e.SourceQN == duplicate.Key);
            }
            return new ExtractionResult { Nodes = _nodes.DistinctBy(n => n.QualifiedName).ToList(),
                Edges = _edges.DistinctBy(e => (e.SourceQN, e.TargetQN, e.Type)).ToList() };
        }

        private void ReadImports()
        {
            foreach (var node in Descendants(root))
            {
                if (_rust && node.Type == "use_declaration") ReadRustUse(node);
                else if (!_rust && node.Type == "import_spec")
                {
                    var path = Literal(node.GetChildForField("path"));
                    if (path is not null)
                        _imports[node.GetChildForField("name")?.Text ?? path.Split('/').Last()] = path;
                }
            }
            if (!_rust)
            {
                var directory = DirectoryOf(_relative);
                while (true)
                {
                    var manifest = directory.Length == 0 ? "go.mod" : directory + "/go.mod";
                    if (context.GoModuleManifests.TryGetValue(manifest, out var content))
                    {
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
            _imports.Clear();
            var scope = RustScope(origin);
            foreach (var use in _rustUses.Where(n => RustScope(n) is var s && scope == s)
                         .OrderBy(n => RustScope(n).Length))
                ReadRustUse(use);
        }

        private void ReadRustUse(Node node)
        {
            var text = node.Text.Trim().TrimEnd(';');
            text = text[(text.IndexOf("use ", StringComparison.Ordinal) + 4)..].Trim();
            ExpandUse(text, "");
        }

        private void ExpandUse(string text, string prefix)
        {
            var brace = text.IndexOf('{');
            if (brace >= 0 && text.EndsWith('}'))
            {
                var nesting = 0; var start = brace + 1;
                for (var i = start; i < text.Length; i++)
                {
                    if (text[i] == '{') nesting++;
                    if ((text[i] == ',' && nesting == 0) || i == text.Length - 1)
                    { ExpandUse(text[start..i].Trim(), prefix + text[..brace]); start = i + 1; }
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
            ct.ThrowIfCancellationRequested();
            if (node.Type is "let_declaration" or "short_var_declaration" or "var_spec" or "assignment_expression" or "assignment_statement")
            {
                var value = node.GetChildForField("value") ?? node.GetChildForField("right");
                if (value?.Type == "expression_list") value = value.NamedChildren.FirstOrDefault();
                var name = node.GetChildForField("pattern") ?? node.GetChildForField("left") ?? node.GetChildForField("name");
                if (name?.Type == "expression_list") name = name.NamedChildren.FirstOrDefault();
                if (value is not null && name is not null)
                {
                    var router = Evaluate(value, env);
                    if (router is not null) env[name.Text] = router;
                    else env.Remove(name.Text);
                }
                return;
            }
            if (node.Type == "call_expression") { Evaluate(node, env); return; }
            if (node.Type is "function_item" or "function_declaration" or "method_declaration" or "closure_expression" or "func_literal") return;
            foreach (var child in node.NamedChildren)
                Process(child, node.Type is "if_expression" or "if_statement" ? new(env, StringComparer.Ordinal) : env);
        }

        private Router? Evaluate(Node node, Dictionary<string, Router> env)
        {
            if (node.Type == "identifier") return env.GetValueOrDefault(node.Text);
            if (node.Type is "reference_expression" or "parenthesized_expression" or "try_expression")
                return node.NamedChildren.Select(n => Evaluate(n, env)).FirstOrDefault(r => r is not null);
            if (node.Type != "call_expression") return null;
            var function = node.GetChildForField("function");
            if (function is null) return null;
            var arguments = node.GetChildForField("arguments")?.NamedChildren.ToList() ?? [];
            var text = function.Text;
            var constructor = _rust ? text.EndsWith("::new") && ExpandPath(text[..^5]) == "axum::Router"
                : _imports.Any(i => i.Value == "github.com/gin-gonic/gin" && (text == i.Key + ".New" || text == i.Key + ".Default"));
            if (constructor) return NewRouter();
            var receiver = function.GetChildForField(_rust ? "value" : "operand");
            var method = function.GetChildForField("field")?.Text;
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
            var result = NewRouter(); parent.Consumed = true; result.Routes.AddRange(parent.Routes);
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
                    result.Routes.AddRange(nested.Routes.Select(r => r with { Path = method == "nest" ? Join(Literal(arguments[0]), r.Path) : r.Path }));
                }
            }
            return result;
        }

        private static void AddRegistration(Router router, Registration registration)
        {
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
            if (node.Type != "call_expression") yield break;
            var function = node.GetChildForField("function");
            var args = node.GetChildForField("arguments")?.NamedChildren.ToList() ?? [];
            if (function is null || args.Count == 0) yield break;
            var receiver = function.GetChildForField("value");
            var name = function.GetChildForField("field")?.Text ?? ExpandPath(function.Text).Split("::").Last();
            if (receiver is not null)
                foreach (var previous in MethodHandlers(receiver)) yield return previous;
            else if (!ExpandPath(function.Text).StartsWith("axum::routing::", StringComparison.Ordinal)) yield break;
            if (name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace" or "connect" or "any")
                yield return (name.ToUpperInvariant(), args[0]);
        }

        private void Emit(Registration registration)
        {
            if (_rust) SetRustImports(registration.Origin);
            var path = registration.Path;
            var handlerText = registration.Handler.Type == "call_expression"
                ? registration.Handler.GetChildForField("function")?.Text ?? registration.Handler.Text
                : registration.Handler.Text;
            var targets = ResolveHandler(handlerText, registration.Origin).ToList();
            var resolved = path is not null && registration.Verb != "UNKNOWN" && targets.Count == 1;
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

        private IEnumerable<GraphNode> ResolveHandler(string expression, Node origin)
        {
            if (!Regex.IsMatch(expression, _rust ? @"^\w+(::\w+)*$" : @"^\w+(\.\w+)*$")) return [];
            var separator = _rust ? "::" : ".";
            var parts = expression.Split(separator);
            // A local variable/parameter can shadow a callable or imported package. Until its
            // semantic target is proven, retain the registration without inventing HANDLES.
            var enclosing = _functions.Where(n => Before(n, origin) && After(n, origin)).LastOrDefault();
            if (enclosing is not null)
            {
                var key = (enclosing.StartPosition.Row, enclosing.StartPosition.Column);
                if (!_shadowDeclarations.TryGetValue(key, out var identifiers))
                    _shadowDeclarations[key] = identifiers = Descendants(enclosing).Where(n => n.Type is "let_declaration" or "short_var_declaration" or "parameter" or "parameter_declaration").ToList();
                if (identifiers.Any(n => Before(n, origin) &&
                    (n.GetChildForField("pattern") ?? n.GetChildForField("name") ?? n.GetChildForField("left"))?.Text == parts[0])) return [];
            }
            var name = (_rust ? ExpandPath(expression).Split("::") : parts)[^1];
            var candidates = availableNodes.Where(n => n.Repository == context.RepositoryName &&
                n.Label is NodeLabel.Function or NodeLabel.Method && n.Name == name &&
                Path.GetExtension(n.FilePath) == (_rust ? ".rs" : ".go"));
            if (!_rust)
            {
                if (parts.Length == 1) return candidates.Where(n => DirectoryOf(n.FilePath) == DirectoryOf(_relative));
                if (parts.Length != 2 || !_imports.TryGetValue(parts[0], out var import)) return [];
                if (_goModule is not null && _goRoot is not null && (import == _goModule || import.StartsWith(_goModule + "/", StringComparison.Ordinal)))
                {
                    var directory = string.Join('/', new[] { _goRoot == "." ? "" : _goRoot, import[_goModule.Length..].Trim('/') }.Where(x => x.Length > 0));
                    return candidates.Where(n => DirectoryOf(n.FilePath) == directory);
                }
                // Package suffixes cannot prove that an external import is this repository's
                // package. Missing or mismatched admitted module identity stays unresolved.
                return [];
            }
            var expanded = ExpandPath(expression);
            parts = expanded.Split("::");
            if (parts.Length == 1)
            {
                // A same-file symbol must also belong to the registering lexical module. Match
                // source declarations, then reuse their exact semantic or fallback identity.
                var scope = RustScope(origin);
                var declarations = _functions.Where(n => n.Type == "function_item" &&
                    n.GetChildForField("name")?.Text == name && RustScope(n) == scope).ToList();
                return candidates.Where(n => n.FilePath == _relative && declarations.Any(d =>
                    n.StartLine == (int)d.StartPosition.Row + 1 || n.StartLine == (int)d.GetChildForField("name")!.StartPosition.Row + 1));
            }
            var srcIndex = _relative.LastIndexOf("/src/", StringComparison.Ordinal);
            var crate = srcIndex >= 0 ? _relative[..(srcIndex + 5)] : _relative.StartsWith("src/") ? "src/" : (DirectoryOf(_relative).Length == 0 ? "" : DirectoryOf(_relative) + "/");
            var module = (_relative[crate.Length..].Replace("/", "::"));
            module = Regex.Replace(module, @"(^|::)(lib|main|mod)\.rs$|\.rs$", "").Trim(':');
            var segments = (module.Length == 0 ? Array.Empty<string>() : module.Split("::")).ToList();
            var lexicalScope = RustScope(origin);
            if (lexicalScope.Length > 0) segments.AddRange(lexicalScope.Split("::"));
            // A bare use path begins at the crate root; an expression path begins at the
            // current module. Explicit self/super/crate imports retain their stated scope.
            if (_imports.ContainsKey(expression.Split("::")[0]) && parts[0] is not ("self" or "super" or "crate")) segments.Clear();
            var path = parts[..^1].ToList();
            if (path[0] == "crate") { segments.Clear(); path.RemoveAt(0); }
            else if (path[0] == "self") path.RemoveAt(0);
            while (path.Count > 0 && path[0] == "super") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); path.RemoveAt(0); }
            segments.AddRange(path);
            var target = crate + string.Join('/', segments);
            var inlineScope = string.Join("::", segments.Skip(module.Length == 0 ? 0 : module.Split("::").Length));
            var inlineDeclarations = (segments.Take(module.Length == 0 ? 0 : module.Split("::").Length).SequenceEqual(module.Length == 0 ? [] : module.Split("::")) ? _functions : []).Where(n => n.GetChildForField("name")?.Text == name && RustScope(n) == inlineScope).ToList();
            return candidates.Where(n => n.FilePath == _relative && inlineDeclarations.Any(d => n.StartLine == (int)d.StartPosition.Row + 1 || n.StartLine == (int)d.GetChildForField("name")!.StartPosition.Row + 1) || n.FilePath == target + ".rs" || n.FilePath == target + "/mod.rs" ||
                (segments.Count == 0 && (n.FilePath == crate + "lib.rs" || n.FilePath == crate + "main.rs")));
        }

        private string RustScope(Node node)
        {
            // Tree-sitter wrappers need not expose Parent: containment bounds give the lexical module.
            return string.Join("::", _rustModules.Where(n => Before(n, node) && After(n, node))
                .OrderBy(n => n.StartPosition.Row).ThenBy(n => n.StartPosition.Column).Select(n => n.GetChildForField("name")?.Text));
        }
        private static bool Before(Node a, Node b) => a.StartPosition.Row < b.StartPosition.Row ||
            a.StartPosition.Row == b.StartPosition.Row && a.StartPosition.Column < b.StartPosition.Column;
        private static bool After(Node a, Node b) => a.EndPosition.Row > b.EndPosition.Row ||
            a.EndPosition.Row == b.EndPosition.Row && a.EndPosition.Column > b.EndPosition.Column;
        private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        private static string? Join(string? prefix, string? path)
        {
            if (prefix is null || path is null) return null;
            var joined = prefix.TrimEnd('/') + "/" + path.TrimStart('/');
            return "/" + joined.TrimStart('/');
        }
        private static string? Literal(Node? node)
        {
            if (node is null || node.Type is not ("string_literal" or "raw_string_literal" or "interpreted_string_literal")) return null;
            var text = node.Text;
            if (text.StartsWith('`') && text.EndsWith('`')) return text[1..^1];
            if (text.StartsWith('r')) { var first = text.IndexOf('"'); var last = text.LastIndexOf('"'); return first >= 0 && last > first ? text[(first + 1)..last] : null; }
            try { return JsonSerializer.Deserialize<string>(text); } catch (JsonException) { return null; }
        }
        private static IEnumerable<Node> Descendants(Node? node)
        {
            if (node is null) yield break;
            yield return node;
            foreach (var child in node.NamedChildren)
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
