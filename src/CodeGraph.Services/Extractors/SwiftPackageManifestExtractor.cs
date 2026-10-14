using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Swift Package Manager manifests. <c>Package.swift</c> is Swift source and there is no Swift
/// toolchain on the indexer, so its <c>.package(...)</c> declarations are read textually (comments
/// and string literals are respected); <c>Package.resolved</c> is JSON in format v1, v2 or v3.
/// Package keys follow SwiftPM's canonical location (lowercased, no scheme, user or <c>.git</c>),
/// so a declared dependency and its resolved pin share a <c>package_key</c>.
/// </summary>
public sealed partial class SwiftPackageManifestExtractor(
    ILogger<SwiftPackageManifestExtractor>? logger = null) : ISwiftPackageManifestExtractor
{
    public const string PackageSwiftFileName = "Package.swift";
    public const string PackageResolvedFileName = "Package.resolved";

    private const string Ecosystem = "swiftpm";
    private readonly ILogger _logger = logger ?? NullLogger<SwiftPackageManifestExtractor>.Instance;

    public string ExtractionVersion => "1";

    public ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        bool hasXcodeProject,
        ExtractorContext context)
    {
        var rootPath = Path.GetFullPath(context.RootPath);
        var ordered = manifests
            .Select(kv => (FullPath: Path.GetFullPath(kv.Key), Content: kv.Value))
            .Select(m => (m.FullPath, m.Content, RelativePath: NormalizePath(Path.GetRelativePath(rootPath, m.FullPath))))
            .OrderBy(m => m.RelativePath, StringComparer.Ordinal)
            .ToList();
        var packageSwifts = ordered.Where(m => IsFile(m.FullPath, PackageSwiftFileName)).ToList();
        var resolvedFiles = ordered.Where(m => IsFile(m.FullPath, PackageResolvedFileName)).ToList();

        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<PendingEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var processed = new List<string>();

        // Pass 1: parse every Package.swift and register the packages it defines, so in-repo
        // path dependencies can point at their definition.
        var parsedManifests = new List<ParsedPackageSwift>();
        var definitionByDirectory = new Dictionary<string, GraphNode>(PathComparer);
        foreach (var (fullPath, content, relativePath) in packageSwifts)
        {
            ParsedPackageSwift parsed;
            try
            {
                parsed = ParsePackageSwift(content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unparseable Swift package manifest {Manifest} in {Project}",
                    relativePath, context.ProjectName);
                continue;
            }

            parsedManifests.Add(parsed with { FullPath = fullPath, RelativePath = relativePath });
            processed.Add(relativePath);
            if (parsed.Name is null)
                continue;

            var key = $"swiftpm:local:{context.ProjectName.ToLowerInvariant()}:{parsed.Name.ToLowerInvariant()}";
            var definition = new GraphNode
            {
                Project = context.ProjectName,
                Label = NodeLabel.Package,
                Name = parsed.Name,
                QualifiedName = QualifiedName(key, null),
                FilePath = relativePath,
                Properties = new Dictionary<string, object>
                {
                    ["ecosystem"] = Ecosystem,
                    ["package_name"] = parsed.Name,
                    ["identity"] = parsed.Name.ToLowerInvariant(),
                    ["version"] = "",
                    ["package_key"] = key,
                    ["source_kind"] = "local",
                    ["source"] = NormalizePath(Path.GetDirectoryName(relativePath) ?? ""),
                    ["is_definition"] = true,
                    ["manifest_path"] = relativePath,
                    ["confidence"] = "high"
                }
            };
            if (nodes.TryAdd(definition.QualifiedName, definition))
            {
                definitionByDirectory[Path.GetDirectoryName(fullPath) ?? rootPath] = definition;
                AddEdge(edges, edgeKeys, new PendingEdge(
                    context.ProjectName,
                    definition.QualifiedName,
                    EdgeType.CONTAINS_PROJECT,
                    new() { ["ecosystem"] = Ecosystem, ["source"] = PackageSwiftFileName }));
            }
        }

        // Pass 2: declared dependencies.
        foreach (var manifest in parsedManifests)
        {
            var owner = manifest.Name is null
                ? null
                : definitionByDirectory.GetValueOrDefault(Path.GetDirectoryName(manifest.FullPath) ?? rootPath);
            var sourceQN = owner?.QualifiedName ?? context.ProjectName;
            foreach (var declaration in manifest.Dependencies)
            {
                var dependency = BuildDeclaredDependency(declaration, manifest, rootPath, context);
                if (dependency is null)
                    continue;

                if (declaration.Path is not null &&
                    definitionByDirectory.TryGetValue(dependency.ResolvedPath!, out var local))
                {
                    var properties = new Dictionary<string, object>(dependency.Node.Properties)
                    {
                        ["canonical_package_name"] = local.Name
                    };
                    AddEdge(edges, edgeKeys, new PendingEdge(
                        sourceQN, local.QualifiedName, EdgeType.REFERENCES_PACKAGE, properties));
                    continue;
                }

                AddDependency(nodes, edges, edgeKeys, sourceQN, dependency.Node);
            }
        }

        // Pass 3: resolved pins from every Package.resolved (root, Xcode project and workspace).
        foreach (var (_, content, relativePath) in resolvedFiles)
        {
            IReadOnlyList<GraphNode> pins;
            try
            {
                pins = ParseResolved(content, relativePath, context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unparseable Swift package resolution file {Manifest} in {Project}",
                    relativePath, context.ProjectName);
                continue;
            }

            processed.Add(relativePath);
            foreach (var pin in pins)
                AddDependency(nodes, edges, edgeKeys, context.ProjectName, pin);
        }

        var metadata = hasXcodeProject
            ? new ProjectMetadata("Swift", "Xcode")
            : packageSwifts.Count > 0
                ? new ProjectMetadata("Swift", "SwiftPM")
                : null;

        return new ExtractionResult
        {
            Nodes = nodes.Values.ToList(),
            Edges = edges,
            ProcessedFiles = processed.Order(StringComparer.Ordinal).ToList(),
            Metadata = metadata
        };
    }

    // ── Package.swift ────────────────────────────────────────────────────

    private static ParsedPackageSwift ParsePackageSwift(string content)
    {
        var (code, masked) = Scan(content);
        var nameMatch = PackageNameRegex().Match(masked);
        var name = nameMatch.Success
            ? ReadStringLiteral(code.Substring(nameMatch.Groups["value"].Index, nameMatch.Groups["value"].Length))
            : null;

        var dependencies = new List<PackageDeclaration>();
        foreach (Match call in PackageCallRegex().Matches(masked))
        {
            var open = call.Index + call.Length - 1;
            var close = FindClosingParen(masked, open);
            if (close < 0)
                continue;

            var declaration = ParseDeclaration(code, masked, open + 1, close);
            if (declaration is not null)
                dependencies.Add(declaration);
        }

        return new ParsedPackageSwift(name, dependencies);
    }

    private static PackageDeclaration? ParseDeclaration(string code, string masked, int start, int end)
    {
        string? url = null, path = null, id = null, name = null;
        Requirement? requirement = null;

        foreach (var (argStart, argEnd) in SplitTopLevel(masked, start, end, ','))
        {
            var maskedArg = masked[argStart..argEnd];
            var arg = code[argStart..argEnd].Trim();
            if (arg.Length == 0)
                continue;

            var labelMatch = LabelRegex().Match(maskedArg);
            var label = labelMatch.Success ? labelMatch.Groups["label"].Value : null;
            var value = labelMatch.Success
                ? code.Substring(argStart + labelMatch.Length, maskedArg.Length - labelMatch.Length).Trim()
                : arg;

            switch (label)
            {
                case "url":
                    url = ReadStringLiteral(value);
                    break;
                case "path":
                    path = ReadStringLiteral(value);
                    break;
                case "id":
                    id = ReadStringLiteral(value);
                    break;
                case "name":
                    name = ReadStringLiteral(value);
                    break;
                case "from":
                    requirement = VersionRequirement("upToNextMajor", value);
                    break;
                case "exact":
                    requirement = VersionRequirement("exact", value);
                    break;
                case "branch":
                    requirement = ReferenceRequirement("branch", value);
                    break;
                case "revision":
                    requirement = ReferenceRequirement("revision", value);
                    break;
                case null or "_":
                    requirement = ParseUnlabeledRequirement(value) ?? requirement;
                    break;
            }
        }

        if (url is null && path is null && id is null)
            return null;
        return new PackageDeclaration(name, url, path, id, requirement);
    }

    private static Requirement? ParseUnlabeledRequirement(string value)
    {
        var range = RangeRegex().Match(value);
        if (range.Success)
        {
            var lower = range.Groups["lower"].Value;
            var upper = range.Groups["upper"].Value;
            return new Requirement($"range:{lower}{range.Groups["op"].Value}{upper}", lower, null);
        }

        var call = RequirementCallRegex().Match(value);
        if (!call.Success)
            return null;

        var argument = call.Groups["arg"].Value;
        return call.Groups["kind"].Value switch
        {
            "upToNextMajor" => VersionRequirement("upToNextMajor", argument),
            "upToNextMinor" => VersionRequirement("upToNextMinor", argument),
            "exact" => VersionRequirement("exact", argument),
            "branch" => ReferenceRequirement("branch", argument),
            "revision" => ReferenceRequirement("revision", argument),
            _ => null
        };
    }

    private static Requirement? VersionRequirement(string kind, string value)
    {
        var version = ReadVersion(value);
        return version is null ? null : new Requirement($"{kind}:{version}", version, null);
    }

    private static Requirement? ReferenceRequirement(string kind, string value)
    {
        var reference = ReadStringLiteral(value);
        if (reference is null)
            return null;
        var gitReference = kind == "revision" ? $"rev:{reference}" : $"branch:{reference}";
        return new Requirement($"{kind}:{reference}", null, gitReference);
    }

    private static string? ReadVersion(string value)
    {
        value = value.Trim();
        var labeled = LabelRegex().Match(value);
        if (labeled.Success && labeled.Groups["label"].Value == "from")
            value = value[labeled.Length..].Trim();

        var literal = ReadStringLiteral(value);
        if (literal is not null)
            return literal;

        var constructed = VersionInitRegex().Match(value);
        return constructed.Success
            ? $"{constructed.Groups["major"].Value}.{constructed.Groups["minor"].Value}.{constructed.Groups["patch"].Value}"
            : null;
    }

    private static DeclaredDependency? BuildDeclaredDependency(
        PackageDeclaration declaration,
        ParsedPackageSwift manifest,
        string rootPath,
        ExtractorContext context)
    {
        string identity, packageKey, sourceKind, source, location;
        string? resolvedPath = null, externalPath = null;

        if (declaration.Path is not null)
        {
            location = declaration.Path;
            var manifestDirectory = Path.GetDirectoryName(manifest.FullPath) ?? rootPath;
            resolvedPath = Path.GetFullPath(Path.Combine(manifestDirectory, declaration.Path));
            source = NormalizePath(Path.GetRelativePath(rootPath, resolvedPath));
            if (!IsWithinDirectory(resolvedPath, rootPath))
                externalPath = source;
            identity = Path.GetFileName(resolvedPath.TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();
            sourceKind = "path";
            packageKey = $"swiftpm:path:{context.ProjectName.ToLowerInvariant()}:{source.ToLowerInvariant()}";
        }
        else if (declaration.Url is not null)
        {
            location = declaration.Url;
            source = CanonicalLocation(location);
            identity = IdentityOf(source);
            sourceKind = IsLocalLocation(location) ? "local" : "remote";
            packageKey = $"swiftpm:scm:{source}";
        }
        else
        {
            location = declaration.Id!;
            source = declaration.Id!.ToLowerInvariant();
            identity = source;
            sourceKind = "registry";
            packageKey = $"swiftpm:registry:{source}";
        }

        var properties = new Dictionary<string, object>
        {
            ["ecosystem"] = Ecosystem,
            ["package_name"] = declaration.Name ?? identity,
            ["identity"] = identity,
            ["version"] = declaration.Requirement?.Version ?? "",
            ["package_key"] = packageKey,
            ["source_kind"] = sourceKind,
            ["source"] = source,
            ["location"] = location,
            ["is_definition"] = false,
            ["dependency_kind"] = "declared",
            ["manifest_path"] = manifest.RelativePath,
            ["confidence"] = "high"
        };
        if (declaration.Requirement is { } requirement)
        {
            properties["version_requirement"] = requirement.Text;
            if (requirement.GitReference is not null)
                properties["git_reference"] = requirement.GitReference;
        }
        if (externalPath is not null)
            properties["external_path"] = externalPath;

        var node = new GraphNode
        {
            Project = context.ProjectName,
            Label = NodeLabel.Package,
            Name = identity,
            QualifiedName = QualifiedName(packageKey, declaration.Requirement?.Text),
            FilePath = manifest.RelativePath,
            Properties = properties
        };
        return new DeclaredDependency(node, resolvedPath);
    }

    // ── Package.resolved ─────────────────────────────────────────────────

    private static IReadOnlyList<GraphNode> ParseResolved(
        string content,
        string relativePath,
        ExtractorContext context)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        var formatVersion = root.TryGetProperty("version", out var versionElement) &&
                            versionElement.ValueKind == JsonValueKind.Number
            ? versionElement.GetInt32()
            : 0;

        JsonElement pinsElement;
        if (root.TryGetProperty("object", out var v1Object) &&
            v1Object.ValueKind == JsonValueKind.Object &&
            v1Object.TryGetProperty("pins", out var v1Pins))
        {
            pinsElement = v1Pins;
            if (formatVersion == 0)
                formatVersion = 1;
        }
        else if (root.TryGetProperty("pins", out var pins))
        {
            pinsElement = pins;
        }
        else
        {
            throw new InvalidDataException($"Package.resolved {relativePath} has no pins.");
        }

        if (pinsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Package.resolved {relativePath} pins are not an array.");

        var result = new List<GraphNode>();
        foreach (var pin in pinsElement.EnumerateArray())
        {
            var node = ReadPin(pin, formatVersion, relativePath, context);
            if (node is not null)
                result.Add(node);
        }

        return result;
    }

    private static GraphNode? ReadPin(
        JsonElement pin,
        int formatVersion,
        string relativePath,
        ExtractorContext context)
    {
        if (pin.ValueKind != JsonValueKind.Object)
            return null;

        // v1: package/repositoryURL; v2+: identity/kind/location.
        var location = GetString(pin, "location") ?? GetString(pin, "repositoryURL") ?? "";
        var kind = GetString(pin, "kind");
        var state = pin.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object
            ? stateElement
            : default;
        var version = GetString(state, "version");
        var revision = GetString(state, "revision");
        var branch = GetString(state, "branch");

        string identity, packageKey, sourceKind, source;
        if (kind == "registry")
        {
            identity = (GetString(pin, "identity") ?? location).ToLowerInvariant();
            if (identity.Length == 0)
                return null;
            source = identity;
            sourceKind = "registry";
            packageKey = $"swiftpm:registry:{identity}";
        }
        else
        {
            if (location.Length == 0)
                return null;
            source = CanonicalLocation(location);
            identity = GetString(pin, "identity")?.ToLowerInvariant() ?? IdentityOf(source);
            sourceKind = kind == "localSourceControl" || (kind is null && IsLocalLocation(location))
                ? "local"
                : "remote";
            packageKey = $"swiftpm:scm:{source}";
        }

        var properties = new Dictionary<string, object>
        {
            ["ecosystem"] = Ecosystem,
            ["package_name"] = GetString(pin, "package") ?? identity,
            ["identity"] = identity,
            ["version"] = version ?? "",
            ["package_key"] = packageKey,
            ["source_kind"] = sourceKind,
            ["source"] = source,
            ["location"] = location,
            ["is_definition"] = false,
            ["dependency_kind"] = "pinned",
            ["resolved_format_version"] = formatVersion,
            ["manifest_path"] = relativePath,
            ["confidence"] = "high"
        };
        if (kind is not null)
            properties["pin_kind"] = kind;
        if (revision is not null)
            properties["revision"] = revision;
        if (branch is not null)
            properties["branch"] = branch;

        return new GraphNode
        {
            Project = context.ProjectName,
            Label = NodeLabel.Package,
            Name = identity,
            QualifiedName = QualifiedName(packageKey, version ?? revision ?? branch),
            FilePath = relativePath,
            Properties = properties
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void AddDependency(
        Dictionary<string, GraphNode> nodes,
        List<PendingEdge> edges,
        HashSet<string> edgeKeys,
        string sourceQN,
        GraphNode dependency)
    {
        // First occurrence in ordinal path order owns the node, so duplicates are deterministic.
        if (!nodes.TryAdd(dependency.QualifiedName, dependency))
            dependency = nodes[dependency.QualifiedName];

        var properties = new Dictionary<string, object>(dependency.Properties)
        {
            ["canonical_package_name"] = dependency.Properties["package_name"]
        };
        AddEdge(edges, edgeKeys, new PendingEdge(
            sourceQN, dependency.QualifiedName, EdgeType.REFERENCES_PACKAGE, properties));
    }

    private static void AddEdge(List<PendingEdge> edges, HashSet<string> edgeKeys, PendingEdge edge)
    {
        if (edgeKeys.Add($"{edge.SourceQN}\n{edge.TargetQN}\n{edge.Type}"))
            edges.Add(edge);
    }

    private static string QualifiedName(string packageKey, string? version) =>
        $"package:{packageKey}@{(string.IsNullOrWhiteSpace(version) ? "*" : version)}";

    /// <summary>
    /// SwiftPM's canonical package location: lowercased, without scheme, user info, trailing
    /// slash or <c>.git</c>; scp-style <c>git@host:path</c> becomes <c>host/path</c>.
    /// </summary>
    internal static string CanonicalLocation(string location)
    {
        var value = location.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
            var at = value.IndexOf('@');
            var slash = value.IndexOf('/');
            if (at >= 0 && (slash < 0 || at < slash))
                value = value[(at + 1)..];
        }
        else if (ScpLocationRegex().Match(value) is { Success: true } scp)
        {
            value = $"{scp.Groups["host"].Value}/{scp.Groups["path"].Value}";
        }

        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value.TrimEnd('/').ToLowerInvariant();
    }

    private static string IdentityOf(string canonicalLocation)
    {
        var slash = canonicalLocation.LastIndexOf('/');
        return slash >= 0 ? canonicalLocation[(slash + 1)..] : canonicalLocation;
    }

    private static bool IsLocalLocation(string location) =>
        location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        location.StartsWith('/') || location.StartsWith('~') || location.StartsWith('.') ||
        (location.Length > 2 && location[1] == ':' && char.IsLetter(location[0]));

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadStringLiteral(string value)
    {
        var match = StringLiteralRegex().Match(value);
        return match.Success
            ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
            : null;
    }

    /// <summary>
    /// Returns the source with comments blanked out (<c>code</c>) and the same text with string
    /// literal contents also blanked (<c>masked</c>), index-aligned, so structure is found in
    /// <c>masked</c> and values read from <c>code</c>.
    /// </summary>
    private static (string Code, string Masked) Scan(string content)
    {
        var code = new StringBuilder(content.Length);
        var masked = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                while (i < content.Length && content[i] != '\n')
                {
                    code.Append(' ');
                    masked.Append(' ');
                    i++;
                }
            }
            else if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                // Swift block comments nest.
                var depth = 0;
                do
                {
                    if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '*')
                    {
                        depth++;
                        AppendBlank(code, masked, content, ref i, 2);
                    }
                    else if (content[i] == '*' && i + 1 < content.Length && content[i + 1] == '/')
                    {
                        depth--;
                        AppendBlank(code, masked, content, ref i, 2);
                    }
                    else
                    {
                        AppendBlank(code, masked, content, ref i, 1);
                    }
                } while (depth > 0 && i < content.Length);
            }
            else if (c == '"')
            {
                var multiline = i + 2 < content.Length && content[i + 1] == '"' && content[i + 2] == '"';
                var delimiter = multiline ? "\"\"\"" : "\"";
                code.Append(delimiter);
                masked.Append(delimiter);
                i += delimiter.Length;
                while (i < content.Length)
                {
                    if (content[i] == '\\' && i + 1 < content.Length)
                    {
                        code.Append(content, i, 2);
                        masked.Append("__");
                        i += 2;
                        continue;
                    }
                    if (string.CompareOrdinal(content, i, delimiter, 0, delimiter.Length) == 0)
                    {
                        code.Append(delimiter);
                        masked.Append(delimiter);
                        i += delimiter.Length;
                        break;
                    }
                    if (!multiline && content[i] == '\n')
                        break;
                    code.Append(content[i]);
                    masked.Append(content[i] == '\n' ? '\n' : '_');
                    i++;
                }
            }
            else
            {
                code.Append(c);
                masked.Append(c);
                i++;
            }
        }

        return (code.ToString(), masked.ToString());
    }

    private static void AppendBlank(StringBuilder code, StringBuilder masked, string content, ref int i, int count)
    {
        for (var n = 0; n < count && i < content.Length; n++, i++)
        {
            var blank = content[i] == '\n' ? '\n' : ' ';
            code.Append(blank);
            masked.Append(blank);
        }
    }

    private static int FindClosingParen(string masked, int open)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] is '(' or '[' or '{')
                depth++;
            else if (masked[i] is ')' or ']' or '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    private static IEnumerable<(int Start, int End)> SplitTopLevel(string masked, int start, int end, char separator)
    {
        var depth = 0;
        var segmentStart = start;
        for (var i = start; i < end; i++)
        {
            switch (masked[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                default:
                    if (masked[i] == separator && depth == 0)
                    {
                        yield return (segmentStart, i);
                        segmentStart = i + 1;
                    }
                    break;
            }
        }
        yield return (segmentStart, end);
    }

    private static bool IsFile(string path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal);

    private static bool IsWithinDirectory(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." ||
               (relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // `.package(` as a call (not `foo.package(`), also `Package.Dependency.package(`.
    [GeneratedRegex(@"(?:(?<![\w\])])|(?<=\bDependency))\.package\s*\(")]
    private static partial Regex PackageCallRegex();

    [GeneratedRegex(@"\bPackage\s*\(\s*name\s*:\s*(?<value>""_*"")")]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z_]\w*)\s*:(?!:)")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^\s*""(?<value>(?:[^""\\]|\\.)*)""\s*$")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"^\s*""(?<lower>[^""]+)""\s*(?<op>\.\.<|\.\.\.)\s*""(?<upper>[^""]+)""\s*$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"^\s*(?:Package\.Dependency\.Requirement)?\.(?<kind>upToNextMajor|upToNextMinor|exact|branch|revision)\s*\((?<arg>.*)\)\s*$", RegexOptions.Singleline)]
    private static partial Regex RequirementCallRegex();

    [GeneratedRegex(@"^\s*Version\s*\(\s*(?<major>\d+)\s*,\s*(?<minor>\d+)\s*,\s*(?<patch>\d+)\s*\)\s*$")]
    private static partial Regex VersionInitRegex();

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s]{2,}):(?<path>[^/].*)$")]
    private static partial Regex ScpLocationRegex();

    private sealed record ParsedPackageSwift(string? Name, IReadOnlyList<PackageDeclaration> Dependencies)
    {
        public string FullPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
    }

    private sealed record PackageDeclaration(
        string? Name,
        string? Url,
        string? Path,
        string? Id,
        Requirement? Requirement);

    private sealed record Requirement(string Text, string? Version, string? GitReference);

    private sealed record DeclaredDependency(GraphNode Node, string? ResolvedPath);
}
