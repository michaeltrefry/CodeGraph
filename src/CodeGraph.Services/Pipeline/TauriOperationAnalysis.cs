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
    internal sealed record Analysis(ExtractionResult Result, IReadOnlyList<string> Diagnostics);
    private sealed record Token(string Text, int Offset, bool String = false);
    private sealed record Definition(string Path, string Module, string Name, string ExposedName, int Line, int EndLine, bool Command, Dictionary<string, string> Imports);
    private sealed record Constant(string Module, string Name, List<Token> Value, Dictionary<string, string> Imports);
    private sealed record Registration(string Path, string Module, string Target, string? Plugin, int Line, Dictionary<string, string> Imports);
    private sealed record RustFile(string Path, string Crate, string CrateName, List<Token> Tokens,
        Dictionary<string, string> Imports, List<Definition> Definitions, List<Registration> Registrations, List<Constant> Constants);
    private sealed record App(string Root, string Frontend, string Config);

    internal static Analysis Analyze(string repository, IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<GraphNode> declarations, IReadOnlyList<GraphNode> references)
    {
        var diagnostics = new List<string>();
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<PendingEdge>();
        var complete = true;
        var apps = new List<App>();
        var manifests = sources.Keys.Where(path => path.EndsWith("Cargo.toml", StringComparison.Ordinal)).ToList();
        var packages = sources.Keys.Where(path => path.EndsWith("package.json", StringComparison.Ordinal)).Select(DirectoryPart).ToList();
        foreach (var config in sources.Keys.Where(path => Path.GetFileName(path) == "tauri.conf.json"))
        {
            try
            {
                using var json = JsonDocument.Parse(sources[config], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = DirectoryPart(config);
                if (!manifests.Contains(Join(root, "Cargo.toml")))
                {
                    diagnostics.Add($"tauriIncomplete: {config} has no admitted Cargo.toml"); complete = false; continue;
                }
                string? dist = null;
                if (json.RootElement.TryGetProperty("build", out var build))
                    foreach (var key in new[] { "frontendDist", "distDir" })
                        if (build.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) dist = value.GetString();
                var frontend = DirectoryPart(root);
                if (dist is not null && !dist.Contains("://", StringComparison.Ordinal))
                {
                    var output = Join(root, dist);
                    frontend = packages.Where(package => Under(output, package)).OrderByDescending(package => package.Length).FirstOrDefault()
                        ?? DirectoryPart(output);
                }
                apps.Add(new App(root, frontend, config));
            }
            catch (JsonException ex) { diagnostics.Add($"tauriIncomplete: {config}: {ex.Message}"); complete = false; }
        }
        if (sources.Keys.Any(path => path.EndsWith("tauri.conf.json5", StringComparison.Ordinal) || path.EndsWith("Tauri.toml", StringComparison.Ordinal)))
        {
            diagnostics.Add("tauriIncomplete: non-JSON Tauri configuration is not statically readable"); complete = false;
        }

        var rustFiles = new List<RustFile>();
        foreach (var (path, source) in sources.Where(pair => pair.Key.EndsWith(".rs", StringComparison.Ordinal)))
        {
            var manifest = manifests.Where(candidate => Under(path, DirectoryPart(candidate))).OrderByDescending(candidate => candidate.Length).FirstOrDefault();
            if (manifest is null) continue;
            var crate = DirectoryPart(manifest);
            var crateName = Regex.Match(sources[manifest], "(?m)^\\s*name\\s*=\\s*\"([^\"]+)\"").Groups[1].Value.Replace('-', '_');
            var tokens = Lex(source, out var lexicalError);
            if (lexicalError is not null) { diagnostics.Add($"tauriIncomplete: {lexicalError} in {path}"); complete = false; continue; }
            if (!Balanced(tokens)) { diagnostics.Add($"tauriIncomplete: unbalanced Rust input {path}"); complete = false; continue; }
            var imports = Imports(tokens);
            var definitions = new List<Definition>();
            var registrations = new List<Registration>();
            var constants = new List<Constant>();
            var relative = path[(crate.Length == 0 ? 0 : crate.Length + 1)..];
            var module = ModuleFor(relative);
            Parse(tokens, 0, tokens.Count, module, path, source, imports, definitions, registrations, constants);
            rustFiles.Add(new RustFile(path, crate, crateName, tokens, imports, definitions, registrations, constants));
        }
        foreach (var app in apps)
        {
            var files = rustFiles.Where(file => file.Crate == app.Root).ToList();
            var allDefinitions = files.SelectMany(file => file.Definitions).ToList();
            var registered = new Dictionary<string, List<(Definition Definition, GraphNode Function, Registration Registration)>>(StringComparer.Ordinal);
            foreach (var file in files) foreach (var registration in file.Registrations)
            {
                var path = ResolvePath(registration.Target, registration.Module, registration.Imports, file.CrateName);
                var candidates = allDefinitions.Where(definition => definition.Command && JoinModule(definition.Module, definition.Name) == path).ToList();
                if (candidates.Count != 1)
                {
                    diagnostics.Add($"tauriIncomplete: registration {registration.Target} in {file.Path}:{registration.Line} resolves to {candidates.Count} admitted command declarations");
                    complete = false; continue;
                }
                var definition = candidates[0];
                var function = FindFunction(declarations, definition.Path, definition.Name, definition.Line);
                if (function is null)
                {
                    diagnostics.Add($"tauriIncomplete: Rust declaration unavailable for registered command {definition.Path}:{definition.Line} {definition.Name}");
                    complete = false; continue;
                }
                var exposed = registration.Plugin is null ? definition.ExposedName : $"plugin:{registration.Plugin}|{definition.ExposedName}";
                if (!registered.TryGetValue(exposed, out var list)) registered[exposed] = list = [];
                list.Add((definition, function, registration));
            }
            foreach (var (name, targets) in registered)
            {
                if (targets.Select(target => target.Function.QualifiedName).Distinct().Count() != 1)
                {
                    diagnostics.Add($"tauriUnresolved: ambiguous registration {app.Root}:{name}"); continue;
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

            foreach (var reference in references.Where(reference => Under(reference.FilePath, app.Frontend)))
            {
                // Shared frontends with multiple native apps are alternatives, not a proven target.
                var owners = apps.Where(candidate => Under(reference.FilePath, candidate.Frontend)).OrderByDescending(candidate => candidate.Frontend.Length).ToList();
                if (owners.Count == 0 || owners[0] != app || owners.Count > 1 && owners[0].Frontend.Length == owners[1].Frontend.Length)
                {
                    diagnostics.Add($"tauriUnresolved: ambiguous app for {reference.FilePath}"); continue;
                }
                var name = Text(reference.Properties.GetValueOrDefault("tauri_name"));
                if (string.IsNullOrEmpty(name)) continue;
                var kind = Text(reference.Properties.GetValueOrDefault("kind"));
                if (kind == "tauri_command_reference")
                {
                    var target = Identity(repository, app.Root, "command", name);
                    if (nodes.ContainsKey(target)) edges.Add(Edge(reference.QualifiedName, target, EdgeType.CALLS, reference.FilePath));
                    else diagnostics.Add($"tauriUnresolved: command {name} is not uniquely registered in {app.Root}");
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
                // Tauri Emitter import is required evidence; unrelated .emit calls are not events.
                if (!file.Imports.Values.Any(value => value is "tauri::Emitter" or "tauri::Manager")) continue;
                for (var index = 1; index + 3 < file.Tokens.Count; index++)
                {
                    if (file.Tokens[index - 1].Text != "." || file.Tokens[index].Text is not ("emit" or "emit_all" or "emit_to") || file.Tokens[index + 1].Text != "(") continue;
                    if (index < 2 || !IsTauriEmitter(file.Tokens, index - 2)) continue;
                    var nameIndex = index + 2;
                    if (file.Tokens[index].Text == "emit_to")
                    {
                        while (nameIndex < file.Tokens.Count && file.Tokens[nameIndex].Text != ",")
                        {
                            if (!file.Tokens[nameIndex].String && file.Tokens[nameIndex].Text is "(" or "[" or "{") nameIndex = Close(file.Tokens, nameIndex);
                            nameIndex++;
                        }
                        nameIndex++;
                    }
                    var eventName = StaticEventName(file, files, nameIndex, Line(sources[file.Path], file.Tokens[index].Offset));
                    if (eventName is null)
                    { diagnostics.Add($"tauriUnresolved: dynamic Rust event in {file.Path}:{Line(sources[file.Path], file.Tokens[index].Offset)}"); continue; }
                    var line = Line(sources[file.Path], file.Tokens[index].Offset);
                    var function = declarations.Where(node => node.FilePath == file.Path && node.Label is NodeLabel.Function or NodeLabel.Method
                        && node.StartLine <= line && node.EndLine >= line).OrderBy(node => node.EndLine - node.StartLine).FirstOrDefault();
                    if (function is null) { diagnostics.Add($"tauriIncomplete: Rust event producer unavailable at {file.Path}:{line}"); complete = false; continue; }
                    edges.Add(Edge(function.QualifiedName, Event(app, eventName), EdgeType.PUBLISHES, file.Path));
                }
            }
        }
        diagnostics.Add($"tauriCoverage: apps={apps.Count}, commands={nodes.Values.Count(node => Text(node.Properties.GetValueOrDefault("kind")) == "tauri_command")}, events={nodes.Values.Count(node => node.Label == NodeLabel.Event)}, bindings={edges.Count}, complete={complete}");
        return new Analysis(new ExtractionResult { Complete = complete, Nodes = nodes.Values.ToList(), Edges = edges }, diagnostics.Take(100).ToList());

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

    private static GraphNode? FindFunction(IReadOnlyList<GraphNode> nodes, string path, string name, int line)
    {
        var matches = nodes.Where(node => node.FilePath == path && node.Name == name && node.Label is NodeLabel.Function or NodeLabel.Method
            && node.StartLine <= line && node.EndLine >= line).OrderBy(node => node.EndLine - node.StartLine).ToList();
        return matches.Count == 1 || matches.Count > 1 && matches[0].EndLine - matches[0].StartLine < matches[1].EndLine - matches[1].StartLine ? matches[0] : null;
    }
    private static bool IsTauriEmitter(List<Token> tokens, int receiver)
    {
        var name = tokens[receiver].Text;
        // The selected apps pass a Tauri AppHandle/Window into producers. Require that typed
        // declaration in the enclosing function; importing Emitter alone does not prove that
        // an unrelated object's identically named emit method is a Tauri event.
        for (var start = receiver; start >= 0; start--)
        {
            if (tokens[start].Text != "fn") continue;
            var parameters = start + 2;
            while (parameters < receiver && tokens[parameters].Text != "(") parameters++;
            var end = Close(tokens, parameters);
            if (end >= receiver) continue;
            for (var cursor = end + 1; cursor + 1 < receiver; cursor++)
                if (tokens[cursor].Text == "let" && (tokens[cursor + 1].Text == name
                    || tokens[cursor + 1].Text == "mut" && cursor + 2 < receiver && tokens[cursor + 2].Text == name)) return false;
            for (var index = parameters; index + 2 < end; index++)
            {
                if (tokens[index].Text != name || tokens[index + 1].Text != ":") continue;
                var typeEnd = index + 2; while (typeEnd < end && tokens[typeEnd].Text != ",") typeEnd++;
                return tokens.Skip(index + 2).Take(typeEnd - index - 2).Any(token => token.Text is "AppHandle" or "Window" or "WebviewWindow" or "App");
            }
            return false;
        }
        return false;
    }
    private static string? StaticEventName(RustFile file, List<RustFile> files, int start, int line)
    {
        if (start >= file.Tokens.Count) return null;
        if (file.Tokens[start].String) return file.Tokens[start].Text;
        var end = start; while (end < file.Tokens.Count && file.Tokens[end].Text is not ("," or ")")) end++;
        var target = string.Concat(file.Tokens.Skip(start).Take(end - start).Select(token => token.Text));
        if (!Regex.IsMatch(target, "^(?:[A-Za-z_][A-Za-z0-9_]*::)*[A-Za-z_][A-Za-z0-9_]*$")) return null;
        var enclosing = file.Definitions.Where(definition => definition.Line <= line && definition.EndLine >= line)
            .OrderBy(definition => definition.EndLine - definition.Line).FirstOrDefault();
        // Parameters/locals can shadow constants. Look only within this producer's lexical body.
        var functionStart = start;
        while (functionStart > 0 && file.Tokens[functionStart].Text != "fn") functionStart--;
        for (var index = functionStart; index + 1 < start; index++)
            if ((file.Tokens[index].Text is "let" or "mut" && file.Tokens[index + 1].Text == target)
                || file.Tokens[index].Text == target && file.Tokens[index + 1].Text == ":") return null;
        var module = enclosing?.Module ?? ModuleFor(file.Path[(file.Crate.Length == 0 ? 0 : file.Crate.Length + 1)..]);
        return Resolve(target, module, enclosing?.Imports ?? file.Imports, new HashSet<string>(StringComparer.Ordinal));

        string? Resolve(string name, string owner, Dictionary<string, string> imports, HashSet<string> seen)
        {
            var resolved = ResolvePath(name, owner, imports, file.CrateName);
            if (!seen.Add(resolved) || seen.Count > 30) return null;
            var matches = files.SelectMany(candidate => candidate.Constants).Where(constant => JoinModule(constant.Module, constant.Name) == resolved).ToList();
            if (matches.Count != 1) return null;
            var constant = matches[0];
            if (constant.Value.Count == 1 && constant.Value[0].String) return constant.Value[0].Text;
            var alias = string.Concat(constant.Value.Select(token => token.Text));
            return Regex.IsMatch(alias, "^(?:[A-Za-z_][A-Za-z0-9_]*::)*[A-Za-z_][A-Za-z0-9_]*$") ? Resolve(alias, constant.Module, constant.Imports, seen) : null;
        }
    }
    private static PendingEdge Edge(string source, string target, EdgeType type, string owner) => new(source, target, type,
        new() { ["extraction_owner"] = Owner, ["extraction_source_file"] = owner, ["transport"] = "tauri", ["confidence"] = "high" });
    private static string Identity(string repo, string app, string kind, string name) => $"tauri:{Uri.EscapeDataString(repo)}:{Uri.EscapeDataString(app)}:{kind}:{Uri.EscapeDataString(name)}";
    private static string? Text(object? value) => value is JsonElement json ? json.GetString() : value?.ToString();
    private static bool Under(string path, string root) => root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private static string DirectoryPart(string path) => Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
    private static string Join(string root, string path) => Path.GetRelativePath("/", Path.GetFullPath(Path.Combine("/", root, path))).Replace('\\', '/');
    private static string JoinModule(string module, string name) => module.Length == 0 ? name : module + "::" + name;
    private static string ModuleFor(string path)
    {
        var parts = path.Replace('\\', '/').Split('/').ToList();
        if (parts[0] == "src") parts.RemoveAt(0);
        parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
        if (parts[^1] is "main" or "lib" or "mod") parts.RemoveAt(parts.Count - 1);
        return string.Join("::", parts);
    }
    private static int Line(string source, int offset) => 1 + source.AsSpan(0, offset).Count('\n');

    private static string ResolvePath(string target, string module, Dictionary<string, string> imports, string crate)
    {
        var parts = target.Split("::").ToList();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        while (parts.Count > 0 && imports.TryGetValue(parts[0], out var import) && aliases.Add(parts[0]))
            parts = import.Split("::").Concat(parts.Skip(1)).ToList();
        if (parts[0] is "crate" || parts[0] == crate) parts.RemoveAt(0);
        else if (parts[0] == "self") { parts.RemoveAt(0); parts = module.Split("::", StringSplitOptions.RemoveEmptyEntries).Concat(parts).ToList(); }
        else if (parts[0] == "super")
        {
            var parent = module.Split("::", StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count > 0 && parts[0] == "super") { parts.RemoveAt(0); if (parent.Count > 0) parent.RemoveAt(parent.Count - 1); }
            parts = parent.Concat(parts).ToList();
        }
        else if (module.Length > 0) parts = module.Split("::").Concat(parts).ToList();
        return string.Join("::", parts);
    }

    private static void Parse(List<Token> tokens, int from, int end, string module, string path, string source,
        Dictionary<string, string> imports, List<Definition> definitions, List<Registration> registrations, List<Constant> constants)
    {
        imports = Imports(tokens, from, end, imports);
        var command = false; string? rename = null;
        for (var index = from; index < end; index++)
        {
            if (tokens[index].Text == "#" && index + 1 < end && tokens[index + 1].Text == "[")
            {
                var close = Close(tokens, index + 1);
                var attribute = string.Concat(tokens.Skip(index + 2).Take(close - index - 2).Select(token => token.Text));
                var commandName = tokens[index + 2].Text;
                if (attribute.StartsWith("tauri::command", StringComparison.Ordinal) || imports.GetValueOrDefault(commandName) == "tauri::command")
                {
                    command = true;
                    for (var cursor = index + 2; cursor + 2 < close; cursor++)
                        if (tokens[cursor].Text == "rename" && tokens[cursor + 1].Text == "=" && tokens[cursor + 2].String) rename = tokens[cursor + 2].Text;
                }
                index = close; continue;
            }
            if (tokens[index].Text == "mod" && index + 2 < end && tokens[index + 2].Text == "{")
            {
                var close = Close(tokens, index + 2);
                Parse(tokens, index + 3, close, JoinModule(module, tokens[index + 1].Text), path, source, imports, definitions, registrations, constants);
                index = close; command = false; rename = null; continue;
            }
            if (tokens[index].Text == "const" && index + 2 < end)
            {
                var equals = index + 2; while (equals < end && tokens[equals].Text is not ("=" or ";" or "{")) equals++;
                if (equals < end && tokens[equals].Text == "=")
                {
                    var semicolon = equals + 1; while (semicolon < end && tokens[semicolon].Text != ";") semicolon++;
                    constants.Add(new Constant(module, tokens[index + 1].Text, tokens.Skip(equals + 1).Take(semicolon - equals - 1).ToList(), imports));
                }
            }
            if (tokens[index].Text == "fn" && index + 1 < end)
            {
                var name = tokens[index + 1].Text;
                var body = index + 2;
                while (body < end && tokens[body].Text is not ("{" or ";")) body++;
                var close = body < end && tokens[body].Text == "{" ? Close(tokens, body) : body;
                definitions.Add(new Definition(path, module, name, rename ?? name, Line(source, tokens[index].Offset),
                    close < tokens.Count ? Line(source, tokens[close].Offset) : Line(source, tokens[index].Offset), command, imports));
                command = false; rename = null;
            }
            if (tokens[index].Text != "invoke_handler" || index < 1 || tokens[index - 1].Text != "." || index + 2 >= end || tokens[index + 1].Text != "(") continue;
            var handlerEnd = Close(tokens, index + 1);
            var start = index + 2;
            var macro = start;
            while (macro < handlerEnd && tokens[macro].Text != "!") macro++;
            if (macro >= handlerEnd || macro + 1 >= handlerEnd || tokens[macro + 1].Text != "[")
            {
                registrations.Add(new Registration(path, module, "<dynamic-handler>", null, Line(source, tokens[index].Offset), imports));
                continue;
            }
            var macroPath = string.Concat(tokens.Skip(start).Take(macro - start).Select(token => token.Text));
            if (macroPath != "tauri::generate_handler" && imports.GetValueOrDefault(macroPath) != "tauri::generate_handler") continue;
            var listEnd = Close(tokens, macro + 1);
            string? plugin = null;
            var pluginMarker = tokens.FindIndex(macro + 2, listEnd - macro - 2, token => token.Text == "plugin");
            if (pluginMarker >= 0 && pluginMarker + 2 < listEnd) plugin = tokens[pluginMarker + 2].Text;
            // Inline plugin builders provide the runtime namespace even without the optional
            // generate_handler plugin annotation used by Tauri's permission optimizer.
            var statementStart = index - 1;
            while (statementStart > from && tokens[statementStart].Text is not (";" or "{" or "}")) statementStart--;
            var prefix = tokens.Skip(statementStart).Take(index - statementStart).ToList();
            for (var cursor = 0; cursor + 6 < prefix.Count; cursor++)
                if (prefix[cursor].Text == "plugin" && prefix[cursor + 2].Text == "Builder")
                    for (var inner = cursor + 3; inner + 2 < prefix.Count; inner++)
                        if (prefix[inner].Text == "new" && prefix[inner + 1].Text == "(" && prefix[inner + 2].String) plugin = prefix[inner + 2].Text;
            var item = macro + 2;
            if (pluginMarker >= 0) item = tokens.FindIndex(pluginMarker, token => token.Text == "]") + 1;
            for (var cursor = item; cursor <= listEnd; cursor++)
            {
                if (cursor != listEnd && tokens[cursor].Text != ",") continue;
                var target = string.Concat(tokens.Skip(item).Take(cursor - item).Select(token => token.Text));
                if (target.Length > 0)
                    registrations.Add(new Registration(path, module, Regex.IsMatch(target, "^(?:[A-Za-z_][A-Za-z0-9_]*::)*[A-Za-z_][A-Za-z0-9_]*$") ? target : "<dynamic-command>", plugin, Line(source, tokens[index].Offset), imports));
                item = cursor + 1;
            }
            index = handlerEnd;
        }
    }

    private static Dictionary<string, string> Imports(List<Token> tokens, int from = 0, int? until = null, Dictionary<string, string>? inherited = null)
    {
        var result = inherited is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new(inherited, StringComparer.Ordinal);
        var limit = until ?? tokens.Count;
        for (var index = from; index < limit; index++)
        {
            if (tokens[index].Text == "{") { index = Close(tokens, index); continue; }
            if (tokens[index].Text != "use") continue;
            var end = index + 1; while (end < limit && tokens[end].Text != ";") end++;
            Expand(index + 1, end, ""); index = end;
        }
        return result;
        void Expand(int start, int end, string prefix)
        {
            var open = tokens.FindIndex(start, end - start, token => token.Text == "{");
            if (open >= 0)
            {
                var root = prefix + string.Concat(tokens.Skip(start).Take(open - start).Select(token => token.Text));
                var close = Close(tokens, open); var item = open + 1; var depth = 0;
                for (var cursor = item; cursor <= close; cursor++)
                {
                    if (tokens[cursor].Text == "{") depth++;
                    if (tokens[cursor].Text == "}") depth--;
                    if (cursor != close && (tokens[cursor].Text != "," || depth != 0)) continue;
                    if (cursor > item) Expand(item, cursor, root); item = cursor + 1;
                }
            }
            else
            {
                var alias = tokens.FindIndex(start, end - start, token => token.Text == "as");
                var value = prefix + string.Concat(tokens.Skip(start).Take((alias < 0 ? end : alias) - start).Select(token => token.Text));
                var name = alias >= 0 ? tokens[alias + 1].Text : value.Split("::")[^1];
                if (name.Length > 0) result[name] = value;
            }
        }
    }

    private static int Close(List<Token> tokens, int open)
    {
        var right = tokens[open].Text switch { "(" => ")", "[" => "]", _ => "}" }; var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].String) continue;
            if (tokens[index].Text == tokens[open].Text) depth++;
            if (tokens[index].Text == right) depth--;
            if (depth == 0) return index;
        }
        return tokens.Count - 1;
    }

    private static bool Balanced(List<Token> tokens)
    {
        var stack = new Stack<string>();
        foreach (var token in tokens.Where(token => !token.String))
        {
            if (token.Text is "(" or "[" or "{") stack.Push(token.Text);
            else if (token.Text is ")" or "]" or "}")
            {
                if (stack.Count == 0) return false;
                if ((stack.Pop(), token.Text) is not (("(", ")") or ("[", "]") or ("{", "}"))) return false;
            }
        }
        return stack.Count == 0;
    }

    private static List<Token> Lex(string source, out string? error)
    {
        var tokens = new List<Token>();
        error = null;
        for (var index = 0; index < source.Length;)
        {
            var start = index;
            if (char.IsWhiteSpace(source[index])) { index++; continue; }
            if (index + 1 < source.Length && source[index..(index + 2)] == "//")
            { while (index < source.Length && source[index] != '\n') index++; continue; }
            if (index + 1 < source.Length && source[index..(index + 2)] == "/*")
            {
                index += 2; var depth = 1;
                while (index + 1 < source.Length && depth > 0)
                {
                    if (source[index..(index + 2)] == "/*") { depth++; index += 2; }
                    else if (source[index..(index + 2)] == "*/") { depth--; index += 2; }
                    else index++;
                }
                if (depth > 0) tokens.Add(new Token("{", start));
                continue;
            }
            var raw = index;
            if (source[index] == 'r')
            {
                raw++; while (raw < source.Length && source[raw] == '#') raw++;
                if (raw < source.Length && source[raw] == '"')
                {
                    var closing = "\"" + new string('#', raw - index - 1);
                    var end = source.IndexOf(closing, raw + 1, StringComparison.Ordinal);
                    if (end < 0) { tokens.Add(new Token("{", start)); break; }
                    tokens.Add(new Token(source[(raw + 1)..end], start, true)); index = end + closing.Length; continue;
                }
            }
            if (source[index] == '"')
            {
                index++;
                while (index < source.Length && source[index] != '"') { if (source[index] == '\\') index++; index++; }
                if (index >= source.Length) { tokens.Add(new Token("{", start)); break; }
                index++;
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
                { tokens.Add(new Token(source[start..end], start, true)); index = end; continue; }
                // Keep lifetime/label tokenization unchanged: apostrophe followed by identifier.
            }
            if (char.IsLetterOrDigit(source[index]) || source[index] == '_')
            {
                index++; while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
                tokens.Add(new Token(source[start..index], start)); continue;
            }
            if (index + 1 < source.Length && source[index..(index + 2)] == "::")
            { tokens.Add(new Token("::", start)); index += 2; continue; }
            tokens.Add(new Token(source[index++].ToString(), start));
        }
        return tokens;
    }

    private static bool TryCharacterLiteral(string source, int start, bool isByte, out int end, out bool lifetime)
    {
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
                    || char.GetUnicodeCategory(source[cursor]) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)) cursor++;
                if (cursor < source.Length && source[cursor] == '\'') return false;
                lifetime = true; return true;
            }
        }
        if (cursor >= source.Length || source[cursor] != '\'') return false;
        end = cursor + 1; return true;
    }
}
