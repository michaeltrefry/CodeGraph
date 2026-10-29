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
/// <para>
/// Each target a <c>Package.swift</c> declares is a <see cref="NodeLabel.Module"/> node (a Swift
/// module) defined by its package, and each <c>.product(name:package:)</c> a target depends on is
/// recorded on the declared dependency it names (<see cref="ProductsProperty"/>), so the indexing
/// pipeline can resolve <c>import</c> declarations to in-repo targets and to dependency packages.
/// </para>
/// <para>
/// Each native target of an Xcode project (<c>*.xcodeproj/project.pbxproj</c>) is a Module node
/// too (ecosystem <see cref="XcodeEcosystem"/>), named by the Swift module it builds
/// (<see cref="XcodeProjectReader"/>), so <c>@testable import App</c> binds to the app target.
/// </para>
/// </summary>
public sealed partial class SwiftPackageManifestExtractor(
    ILogger<SwiftPackageManifestExtractor>? logger = null) : ISwiftPackageManifestExtractor
{
    public const string PackageSwiftFileName = "Package.swift";
    public const string PackageResolvedFileName = "Package.resolved";

    /// <summary>
    /// On a declared dependency: comma-separated product names the manifest's targets use from it
    /// (<c>.product(name: "Logging", package: "swift-log")</c>).
    /// </summary>
    public const string ProductsProperty = "swift_products";

    /// <summary>On a target's Module node: the Swift module name (the target name).</summary>
    public const string ModuleNameProperty = "module_name";

    /// <summary>
    /// On an Xcode target's Module node: the repository-relative directory holding its
    /// <c>.xcodeproj</c> (empty at the root).
    /// </summary>
    public const string ProjectDirectoryProperty = "project_directory";

    /// <summary>Ecosystem of the Module nodes of Xcode project targets.</summary>
    public const string XcodeEcosystem = "xcode";

    private const string Ecosystem = "swiftpm";
    private readonly ILogger _logger = logger ?? NullLogger<SwiftPackageManifestExtractor>.Instance;

    /// <remarks>2 (sc-26145): a target's <c>sources:</c> subpaths are recorded (<see cref="SourcePathsProperty"/>).</remarks>
    public string ExtractionVersion => "2";

    /// <summary>
    /// On a SwiftPM target's Module node: comma-separated repository-relative paths its
    /// <c>sources:</c> argument limits it to, when present (several targets can share one <c>path:</c>).
    /// </summary>
    public const string SourcePathsProperty = "source_paths";

    public ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        bool hasXcodeProject,
        ExtractorContext context)
    {
        var rootPath = Path.GetFullPath(context.RootPath);
        var ordered = manifests
            .Select(kv => (FullPath: Path.GetFullPath(kv.Key), Content: kv.Value))
            .Select(m => (m.FullPath, m.Content, RelativePath: NormalizePath(Path.GetRelativePath(rootPath, m.FullPath))))
            // Shallowest first, so the repository's own root manifest wins over nested copies
            // (worktrees, vendored checkouts) that define the same package.
            .OrderBy(m => m.RelativePath.Count(ch => ch == '/'))
            .ThenBy(m => m.RelativePath, StringComparer.Ordinal)
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
                parsed = ParsePackageSwift(content, relativePath, context.RepositoryName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unparseable Swift package manifest {Manifest} in {Repository}",
                    relativePath, context.RepositoryName);
                continue;
            }

            parsedManifests.Add(parsed with { FullPath = fullPath, RelativePath = relativePath });
            processed.Add(relativePath);
            if (parsed.Name is null)
            {
                AddTargets(nodes, edges, edgeKeys, parsed, relativePath, owner: null, context);
                continue;
            }

            var key = $"swiftpm:local:{context.RepositoryName.ToLowerInvariant()}:{parsed.Name.ToLowerInvariant()}";
            var definition = new GraphNode
            {
                Repository = context.RepositoryName,
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
            var added = nodes.TryAdd(definition.QualifiedName, definition);
            // A second manifest defining the same package shares the first one's definition.
            definitionByDirectory[Path.GetDirectoryName(fullPath) ?? rootPath] = nodes[definition.QualifiedName];
            if (added)
            {
                AddEdge(edges, edgeKeys, new PendingEdge(
                    context.RepositoryName,
                    definition.QualifiedName,
                    EdgeType.CONTAINS_PROJECT,
                    new() { ["ecosystem"] = Ecosystem, ["source"] = PackageSwiftFileName }));
            }

            AddTargets(nodes, edges, edgeKeys, parsed, relativePath, nodes[definition.QualifiedName], context);
        }

        // Pass 2: declared dependencies.
        foreach (var manifest in parsedManifests)
        {
            var owner = manifest.Name is null
                ? null
                : definitionByDirectory.GetValueOrDefault(Path.GetDirectoryName(manifest.FullPath) ?? rootPath);
            var sourceQN = owner?.QualifiedName ?? context.RepositoryName;
            var declaredNodes = new List<(PackageDeclaration Declaration, GraphNode Node)>();
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
                declaredNodes.Add((declaration, nodes[dependency.Node.QualifiedName]));
            }

            RecordProducts(manifest, declaredNodes);
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
                _logger.LogWarning(ex, "Skipping unparseable Swift package resolution file {Manifest} in {Repository}",
                    relativePath, context.RepositoryName);
                continue;
            }

            processed.Add(relativePath);
            foreach (var pin in pins)
                AddDependency(nodes, edges, edgeKeys, context.RepositoryName, pin);
        }

        // Pass 4: native targets of every Xcode project, as the Swift modules they build.
        foreach (var (_, content, relativePath) in ordered.Where(m => IsFile(m.FullPath, XcodeProjectReader.ProjectFileName)))
        {
            IReadOnlyList<XcodeNativeTarget> targets;
            try
            {
                targets = XcodeProjectReader.ReadNativeTargets(content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unparseable Xcode project {Manifest} in {Repository}",
                    relativePath, context.RepositoryName);
                continue;
            }

            processed.Add(relativePath);
            AddXcodeTargets(nodes, edges, edgeKeys, targets, relativePath, context);
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

    private ParsedPackageSwift ParsePackageSwift(string content, string relativePath, string repository)
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
            else
                _logger.LogDebug(
                    "Skipping .package(...) without a literal url, path or id in {Manifest} of {Repository}: {Declaration}",
                    relativePath, repository, code[call.Index..(close + 1)].Trim());
        }

        return new ParsedPackageSwift(name, dependencies, ParseTargets(code, masked));
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
            packageKey = $"swiftpm:path:{context.RepositoryName.ToLowerInvariant()}:{source.ToLowerInvariant()}";
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
            Repository = context.RepositoryName,
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
            Repository = context.RepositoryName,
            Label = NodeLabel.Package,
            Name = identity,
            QualifiedName = QualifiedName(packageKey, version ?? revision ?? branch),
            FilePath = relativePath,
            Properties = properties
        };
    }

    // ── Targets ──────────────────────────────────────────────────────────

    /// <summary>
    /// One Module node per declared target, at <c>{repository}:{manifest}#target:{name}</c>, defined by
    /// the manifest's package (or the repository when the package name is not static).
    /// </summary>
    /// <summary>
    /// One Module node per native target of an Xcode project (ecosystem <see cref="XcodeEcosystem"/>),
    /// named by the Swift module it builds. Its <see cref="ProjectDirectoryProperty"/> is the
    /// directory holding the <c>.xcodeproj</c>, the root the import resolution measures nearness from.
    /// </summary>
    private static void AddXcodeTargets(
        Dictionary<string, GraphNode> nodes,
        List<PendingEdge> edges,
        HashSet<string> edgeKeys,
        IReadOnlyList<XcodeNativeTarget> targets,
        string relativePath,
        ExtractorContext context)
    {
        var projectDirectory = NormalizePath(Path.GetDirectoryName(Path.GetDirectoryName(relativePath) ?? "") ?? "");
        foreach (var target in targets)
        {
            var properties = new Dictionary<string, object>
            {
                ["ecosystem"] = XcodeEcosystem,
                ["language"] = "Swift",
                [ModuleNameProperty] = target.ModuleName,
                ["target_name"] = target.Name,
                ["manifest_path"] = relativePath,
                [ProjectDirectoryProperty] = projectDirectory,
                ["confidence"] = "high"
            };
            if (target.ProductType is { Length: > 0 } productType)
                properties["target_kind"] = productType;

            var module = new GraphNode
            {
                Repository = context.RepositoryName,
                Label = NodeLabel.Module,
                Name = target.ModuleName,
                QualifiedName = $"{context.RepositoryName}:{relativePath}#target:{target.Name}",
                FilePath = relativePath,
                Properties = properties
            };
            if (!nodes.TryAdd(module.QualifiedName, module))
                continue;

            AddEdge(edges, edgeKeys, new PendingEdge(
                context.RepositoryName,
                module.QualifiedName,
                EdgeType.DEFINES,
                new() { ["ecosystem"] = XcodeEcosystem, ["source"] = XcodeProjectReader.ProjectFileName }));
        }
    }

    private static void AddTargets(
        Dictionary<string, GraphNode> nodes,
        List<PendingEdge> edges,
        HashSet<string> edgeKeys,
        ParsedPackageSwift manifest,
        string relativePath,
        GraphNode? owner,
        ExtractorContext context)
    {
        var manifestDirectory = NormalizePath(Path.GetDirectoryName(relativePath) ?? "");
        foreach (var target in manifest.Targets)
        {
            var properties = new Dictionary<string, object>
            {
                ["ecosystem"] = Ecosystem,
                ["language"] = "Swift",
                [ModuleNameProperty] = target.Name,
                ["target_kind"] = target.Kind,
                ["manifest_path"] = relativePath,
                ["confidence"] = "high"
            };
            if (owner is not null)
                properties["package_name"] = owner.Name;
            var sourceDirectory = target.Path ?? DefaultTargetDirectory(target);
            if (sourceDirectory is not null)
            {
                var sourcePath = NormalizePath(
                    manifestDirectory.Length == 0 ? sourceDirectory : $"{manifestDirectory}/{sourceDirectory}");
                properties["source_path"] = sourcePath;
                if (target.Sources is { Count: > 0 } sources)
                {
                    properties[SourcePathsProperty] = string.Join(',', sources
                        .Select(source => NormalizePath(sourcePath.Length == 0 ? source : $"{sourcePath}/{source}"))
                        .Distinct(StringComparer.Ordinal));
                }
            }

            var module = new GraphNode
            {
                Repository = context.RepositoryName,
                Label = NodeLabel.Module,
                Name = target.Name,
                QualifiedName = $"{context.RepositoryName}:{relativePath}#target:{target.Name}",
                FilePath = relativePath,
                Properties = properties
            };
            if (!nodes.TryAdd(module.QualifiedName, module))
                continue;

            AddEdge(edges, edgeKeys, new PendingEdge(
                owner?.QualifiedName ?? context.RepositoryName,
                module.QualifiedName,
                EdgeType.DEFINES,
                new() { ["ecosystem"] = Ecosystem, ["target_kind"] = target.Kind }));
        }
    }

    // SwiftPM's default source directory of a target that does not set `path:`.
    private static string? DefaultTargetDirectory(TargetDeclaration target) => target.Kind switch
    {
        "testTarget" => $"Tests/{target.Name}",
        "plugin" => $"Plugins/{target.Name}",
        "binaryTarget" => null,
        _ => $"Sources/{target.Name}"
    };

    /// <summary>
    /// Records on each declared dependency the products the manifest's targets use from it: the
    /// <c>package:</c> argument names the dependency by its identity or its declared name.
    /// </summary>
    private static void RecordProducts(
        ParsedPackageSwift manifest,
        IReadOnlyList<(PackageDeclaration Declaration, GraphNode Node)> declared)
    {
        foreach (var (product, package) in manifest.Targets.SelectMany(target => target.Products))
        {
            var dependency = declared.FirstOrDefault(entry =>
                string.Equals((string)entry.Node.Properties["identity"], package, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Declaration.Name, package, StringComparison.OrdinalIgnoreCase));
            if (dependency.Node is null)
                continue;

            var products = dependency.Node.Properties.GetValueOrDefault(ProductsProperty) is string existing
                ? existing.Split(',').ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            products.Add(product);
            dependency.Node.Properties[ProductsProperty] = string.Join(',', products.Order(StringComparer.Ordinal));
        }
    }

    private static List<TargetDeclaration> ParseTargets(string code, string masked)
    {
        var targets = new List<TargetDeclaration>();
        var consumedUntil = -1;
        foreach (Match call in TargetCallRegex().Matches(masked))
        {
            // A `.target(name:)` dependency inside an already-read target is not a declaration.
            if (call.Index < consumedUntil)
                continue;
            var open = call.Index + call.Length - 1;
            var close = FindClosingParen(masked, open);
            if (close < 0)
                continue;
            consumedUntil = close;

            string? name = null, path = null;
            List<string>? sources = null;
            var products = new List<(string Product, string Package)>();
            foreach (var (argStart, argEnd) in SplitTopLevel(masked, open + 1, close, ','))
            {
                var labelMatch = LabelRegex().Match(masked[argStart..argEnd]);
                if (!labelMatch.Success)
                    continue;
                var valueStart = argStart + labelMatch.Length;
                var value = code[valueStart..argEnd];
                switch (labelMatch.Groups["label"].Value)
                {
                    case "name":
                        name = ReadStringLiteral(value);
                        break;
                    case "path":
                        path = ReadStringLiteral(value);
                        break;
                    case "dependencies":
                        products.AddRange(ParseProductDependencies(code, masked, valueStart, argEnd));
                        break;
                    case "sources":
                        sources = ParseStringArray(code, masked, valueStart, argEnd);
                        break;
                }
            }

            if (name is not null)
                targets.Add(new TargetDeclaration(call.Groups["kind"].Value, name, path, products, sources));
        }

        return targets;
    }

    /// <summary>The string literals of an array argument (<c>sources: ["A", "B/File.swift"]</c>); null when it is not literal.</summary>
    private static List<string>? ParseStringArray(string code, string masked, int start, int end)
    {
        var open = masked.IndexOf('[', start);
        if (open < 0 || open >= end)
            return null;
        var close = FindClosingParen(masked, open);
        if (close < 0)
            return null;

        var values = new List<string>();
        foreach (var (elementStart, elementEnd) in SplitTopLevel(masked, open + 1, close, ','))
        {
            if (code[elementStart..elementEnd].Trim().Length == 0)
                continue;
            if (ReadStringLiteral(code[elementStart..elementEnd]) is not { } value)
                return null;
            values.Add(value.Trim('/'));
        }

        return values;
    }

    /// <summary>The <c>.product(name:package:)</c> entries of a target's dependency array.</summary>
    private static IEnumerable<(string Product, string Package)> ParseProductDependencies(
        string code, string masked, int start, int end)
    {
        var open = masked.IndexOf('[', start);
        if (open < 0 || open >= end)
            yield break;
        var close = FindClosingParen(masked, open);
        if (close < 0)
            yield break;

        foreach (var (elementStart, elementEnd) in SplitTopLevel(masked, open + 1, close, ','))
        {
            var product = ProductCallRegex().Match(masked[elementStart..elementEnd]);
            if (!product.Success)
                continue;
            var callOpen = elementStart + product.Length - 1;
            var callClose = FindClosingParen(masked, callOpen);
            if (callClose < 0)
                continue;

            string? name = null, package = null;
            foreach (var (argStart, argEnd) in SplitTopLevel(masked, callOpen + 1, callClose, ','))
            {
                var labelMatch = LabelRegex().Match(masked[argStart..argEnd]);
                if (!labelMatch.Success)
                    continue;
                var value = code[(argStart + labelMatch.Length)..argEnd];
                if (labelMatch.Groups["label"].Value == "name")
                    name = ReadStringLiteral(value);
                else if (labelMatch.Groups["label"].Value == "package")
                    package = ReadStringLiteral(value);
            }

            if (name is not null && package is not null)
                yield return (name, package);
        }
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

    /// <summary>
    /// The value of a single-line string literal, plain (<c>"..."</c>) or raw (<c>#"..."#</c>).
    /// Null for anything else, including interpolated literals whose value is not static.
    /// </summary>
    private static string? ReadStringLiteral(string value)
    {
        var raw = RawStringLiteralRegex().Match(value);
        if (raw.Success)
        {
            var literal = raw.Groups["value"].Value;
            var interpolation = "\\" + raw.Groups["hashes"].Value + "(";
            return literal.Contains(interpolation, StringComparison.Ordinal) ? null : literal;
        }

        var match = StringLiteralRegex().Match(value);
        if (!match.Success || match.Groups["value"].Value.Contains("\\(", StringComparison.Ordinal))
            return null;
        return match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
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
            else if (c == '"' || RawStringStart(content, i) > 0)
            {
                // Raw strings (#"..."#) close on the quote followed by the same number of hashes
                // and have no escapes.
                var hashes = RawStringStart(content, i);
                var quoteAt = i + hashes;
                var multiline = quoteAt + 2 < content.Length &&
                                content[quoteAt + 1] == '"' && content[quoteAt + 2] == '"';
                var quotes = multiline ? "\"\"\"" : "\"";
                var opening = new string('#', hashes) + quotes;
                var delimiter = quotes + new string('#', hashes);
                code.Append(opening);
                masked.Append(opening);
                i += opening.Length;
                while (i < content.Length)
                {
                    if (hashes == 0 && content[i] == '\\' && i + 1 < content.Length)
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

    /// <summary>Number of leading hashes when a raw string literal starts at <paramref name="i"/>, else 0.</summary>
    private static int RawStringStart(string content, int i)
    {
        var j = i;
        while (j < content.Length && content[j] == '#')
            j++;
        return j > i && j < content.Length && content[j] == '"' ? j - i : 0;
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

    // A target declaration: `.target(`, `.executableTarget(`, ... (also `Target.target(`).
    [GeneratedRegex(@"(?:(?<![\w\])])|(?<=\bTarget))\.(?<kind>target|executableTarget|testTarget|macro|plugin|binaryTarget|systemLibrary)\s*\(")]
    private static partial Regex TargetCallRegex();

    [GeneratedRegex(@"^\s*(?:Target\.Dependency)?\.product\s*\(")]
    private static partial Regex ProductCallRegex();

    [GeneratedRegex(@"\bPackage\s*\(\s*name\s*:\s*(?<value>(?<hashes>#*)""_*""\k<hashes>)")]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z_]\w*)\s*:(?!:)")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^\s*""(?<value>(?:[^""\\]|\\.)*)""\s*$")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"^\s*(?<hashes>#+)""(?<value>[^\n]*)""\k<hashes>\s*$")]
    private static partial Regex RawStringLiteralRegex();

    [GeneratedRegex(@"^\s*""(?<lower>[^""]+)""\s*(?<op>\.\.<|\.\.\.)\s*""(?<upper>[^""]+)""\s*$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"^\s*(?:Package\.Dependency\.Requirement)?\.(?<kind>upToNextMajor|upToNextMinor|exact|branch|revision)\s*\((?<arg>.*)\)\s*$", RegexOptions.Singleline)]
    private static partial Regex RequirementCallRegex();

    [GeneratedRegex(@"^\s*Version\s*\(\s*(?<major>\d+)\s*,\s*(?<minor>\d+)\s*,\s*(?<patch>\d+)\s*\)\s*$")]
    private static partial Regex VersionInitRegex();

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s]{2,}):(?<path>[^/].*)$")]
    private static partial Regex ScpLocationRegex();

    private sealed record ParsedPackageSwift(
        string? Name,
        IReadOnlyList<PackageDeclaration> Dependencies,
        IReadOnlyList<TargetDeclaration> Targets)
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

    private sealed record TargetDeclaration(
        string Kind,
        string Name,
        string? Path,
        IReadOnlyList<(string Product, string Package)> Products,
        IReadOnlyList<string>? Sources = null);

    private sealed record DeclaredDependency(GraphNode Node, string? ResolvedPath);
}
