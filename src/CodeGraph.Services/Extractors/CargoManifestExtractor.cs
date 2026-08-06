using Tomlyn;
using Tomlyn.Model;
using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

public sealed class CargoManifestExtractor : ICargoManifestExtractor
{
    private static readonly string[] DependencySections =
        ["dependencies", "dev-dependencies", "build-dependencies"];

    public ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        ExtractorContext context)
    {
        var parsed = manifests
            .Select(kv => ParseManifest(kv.Key, kv.Value, context.RootPath))
            .OrderBy(m => m.RelativePath, StringComparer.Ordinal)
            .ToList();

        if (parsed.Count == 0)
            return new ExtractionResult { Metadata = new ProjectMetadata("Rust", "Cargo") };

        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<PendingEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var packageByDirectory = new Dictionary<string, PackageDefinition>(PathComparer);

        foreach (var manifest in parsed)
        {
            var workspace = FindWorkspaceManifest(manifest, parsed);
            var definition = ReadPackageDefinition(manifest, workspace, context);
            if (definition is null)
                continue;

            packageByDirectory[Path.GetDirectoryName(manifest.FullPath) ?? context.RootPath] = definition;
            nodes[definition.Node.QualifiedName] = definition.Node;
            AddEdge(edges, edgeKeys, new PendingEdge(
                context.ProjectName,
                definition.Node.QualifiedName,
                EdgeType.CONTAINS_PROJECT,
                new() { ["ecosystem"] = "cargo", ["source"] = "Cargo.toml" }));
        }

        foreach (var manifest in parsed)
        {
            var workspace = FindWorkspaceManifest(manifest, parsed);
            var owner = packageByDirectory.GetValueOrDefault(
                Path.GetDirectoryName(manifest.FullPath) ?? context.RootPath);
            var sourceQN = owner?.Node.QualifiedName ?? context.ProjectName;
            var workspaceDependencies = ReadDependencyTable(workspace?.Workspace, "dependencies");

            foreach (var dependency in ReadDependencies(
                         manifest,
                         workspace,
                         workspaceDependencies,
                         packageByDirectory,
                         context))
            {
                if (!nodes.ContainsKey(dependency.TargetQualifiedName))
                    nodes[dependency.TargetQualifiedName] = dependency.TargetNode;

                AddEdge(edges, edgeKeys, new PendingEdge(
                    sourceQN,
                    dependency.TargetQualifiedName,
                    EdgeType.REFERENCES_PACKAGE,
                    dependency.EdgeProperties));
            }
        }

        return new ExtractionResult
        {
            Nodes = nodes.Values.ToList(),
            Edges = edges,
            Metadata = new ProjectMetadata("Rust", "Cargo")
        };
    }

    private static ParsedManifest ParseManifest(string fullPath, string content, string rootPath)
    {
        var normalizedFullPath = Path.GetFullPath(fullPath);
        var relativePath = NormalizePath(Path.GetRelativePath(rootPath, normalizedFullPath));
        var model = TomlSerializer.Deserialize<TomlTable>(content)
            ?? throw new InvalidDataException($"Cargo manifest {relativePath} did not produce a TOML model.");

        return new ParsedManifest(
            normalizedFullPath,
            relativePath,
            model,
            GetTable(model, "package"),
            GetTable(model, "workspace"));
    }

    private static ParsedManifest? FindWorkspaceManifest(
        ParsedManifest manifest,
        IReadOnlyList<ParsedManifest> manifests)
    {
        var manifestDirectory = Path.GetDirectoryName(manifest.FullPath) ?? "";
        return manifests
            .Where(candidate => candidate.Workspace is not null)
            .Where(candidate => IsWithinDirectory(
                manifestDirectory,
                Path.GetDirectoryName(candidate.FullPath) ?? ""))
            .OrderByDescending(candidate =>
                (Path.GetDirectoryName(candidate.FullPath) ?? "").Length)
            .FirstOrDefault();
    }

    private static PackageDefinition? ReadPackageDefinition(
        ParsedManifest manifest,
        ParsedManifest? workspace,
        ExtractorContext context)
    {
        if (manifest.Package is null)
            return null;

        var name = GetString(manifest.Package, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var workspacePackage = GetTable(workspace?.Workspace, "package");
        var version = ReadInheritedString(manifest.Package, workspacePackage, "version") ?? "";
        var publish = ReadInheritedValue(manifest.Package, workspacePackage, "publish");
        var definitionSource = publish is false ? "workspace" : "crates.io";
        var packageKey = definitionSource == "crates.io"
            ? CargoPackageKey.Registry("crates.io", name)
            : CargoPackageKey.Workspace(context.ProjectName, name);
        var qn = CargoPackageKey.QualifiedName(packageKey, version);

        var properties = new Dictionary<string, object>
        {
            ["ecosystem"] = "cargo",
            ["package_name"] = name,
            ["version"] = version,
            ["package_key"] = packageKey,
            ["source_kind"] = definitionSource == "crates.io" ? "registry" : "workspace",
            ["source"] = definitionSource,
            ["is_definition"] = true,
            ["manifest_path"] = manifest.RelativePath,
            ["confidence"] = "high"
        };

        return new PackageDefinition(new GraphNode
        {
            Project = context.ProjectName,
            Label = NodeLabel.Package,
            Name = name,
            QualifiedName = qn,
            FilePath = manifest.RelativePath,
            Properties = properties
        });
    }

    private static IEnumerable<DependencyReference> ReadDependencies(
        ParsedManifest manifest,
        ParsedManifest? workspace,
        TomlTable? workspaceDependencies,
        IReadOnlyDictionary<string, PackageDefinition> packageByDirectory,
        ExtractorContext context)
    {
        foreach (var section in DependencySections)
        {
            var table = ReadDependencyTable(manifest.Model, section);
            foreach (var dependency in ReadDependencyEntries(
                         table, section, target: null, manifest, workspace,
                         workspaceDependencies, packageByDirectory, context))
            {
                yield return dependency;
            }
        }

        var targets = GetTable(manifest.Model, "target");
        if (targets is null)
            yield break;

        foreach (var (targetName, targetValue) in targets)
        {
            if (targetValue is not TomlTable targetTable)
                continue;

            foreach (var section in DependencySections)
            {
                var table = ReadDependencyTable(targetTable, section);
                foreach (var dependency in ReadDependencyEntries(
                             table, section, targetName, manifest, workspace,
                             workspaceDependencies, packageByDirectory, context))
                {
                    yield return dependency;
                }
            }
        }
    }

    private static IEnumerable<DependencyReference> ReadDependencyEntries(
        TomlTable? table,
        string section,
        string? target,
        ParsedManifest manifest,
        ParsedManifest? workspace,
        TomlTable? workspaceDependencies,
        IReadOnlyDictionary<string, PackageDefinition> packageByDirectory,
        ExtractorContext context)
    {
        if (table is null)
            yield break;

        foreach (var (localName, rawValue) in table)
        {
            var specification = ReadDependencySpecification(
                localName,
                rawValue,
                manifest,
                workspace,
                workspaceDependencies);

            var targetDefinition = specification.ResolvedPath is null
                ? null
                : packageByDirectory.GetValueOrDefault(specification.ResolvedPath);
            if (targetDefinition is not null)
            {
                specification = specification with
                {
                    PackageName = targetDefinition.Node.Name,
                    PackageKey = targetDefinition.Node.Properties["package_key"].ToString()!,
                    TargetQualifiedName = targetDefinition.Node.QualifiedName,
                    Version = targetDefinition.Node.Properties["version"].ToString() ?? specification.Version
                };
            }

            var targetQN = specification.TargetQualifiedName
                ?? CargoPackageKey.QualifiedName(specification.PackageKey, specification.Version);
            var nodeProperties = new Dictionary<string, object>
            {
                ["ecosystem"] = "cargo",
                ["package_name"] = specification.PackageName,
                ["local_name"] = localName,
                ["version"] = specification.Version ?? "",
                ["package_key"] = specification.PackageKey,
                ["source_kind"] = specification.SourceKind,
                ["source"] = specification.Source,
                ["is_definition"] = false,
                ["manifest_path"] = manifest.RelativePath,
                ["dependency_scope"] = section,
                ["workspace_inherited"] = specification.WorkspaceInherited,
                ["optional"] = specification.Optional,
                ["confidence"] = "high"
            };
            if (target is not null)
                nodeProperties["target"] = target;
            if (specification.GitReference is not null)
                nodeProperties["git_reference"] = specification.GitReference;

            var edgeProperties = new Dictionary<string, object>(nodeProperties)
            {
                ["canonical_package_name"] = specification.PackageName
            };

            yield return new DependencyReference(
                targetQN,
                new GraphNode
                {
                    Project = context.ProjectName,
                    Label = NodeLabel.Package,
                    Name = specification.PackageName,
                    QualifiedName = targetQN,
                    FilePath = manifest.RelativePath,
                    Properties = nodeProperties
                },
                edgeProperties);
        }
    }

    private static DependencySpecification ReadDependencySpecification(
        string localName,
        object? rawValue,
        ParsedManifest manifest,
        ParsedManifest? workspace,
        TomlTable? workspaceDependencies)
    {
        var table = rawValue as TomlTable;
        var workspaceInherited = GetBool(table, "workspace") == true;

        DependencySpecification? inherited = null;
        if (workspaceInherited && workspaceDependencies is not null &&
            workspaceDependencies.TryGetValue(localName, out var inheritedValue))
        {
            inherited = ReadDependencySpecification(
                localName,
                inheritedValue,
                workspace ?? manifest,
                workspace: null,
                workspaceDependencies: null);
        }

        var packageName = GetString(table, "package")
            ?? inherited?.PackageName
            ?? localName;
        var version = rawValue as string
            ?? GetString(table, "version")
            ?? inherited?.Version;
        var git = GetString(table, "git") ?? inherited?.Git;
        var registry = GetString(table, "registry") ?? inherited?.Registry;
        var path = GetString(table, "path") ?? inherited?.Path;
        var branch = GetString(table, "branch") ?? inherited?.Branch;
        var tag = GetString(table, "tag") ?? inherited?.Tag;
        var rev = GetString(table, "rev") ?? inherited?.Rev;
        var optional = GetBool(table, "optional") ?? inherited?.Optional ?? false;

        string sourceKind;
        string source;
        string packageKey;
        string? resolvedPath = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            sourceKind = "path";
            var declaringDirectory = Path.GetDirectoryName(
                inherited is not null && workspace is not null
                    ? workspace.FullPath
                    : manifest.FullPath) ?? "";
            resolvedPath = Path.GetFullPath(Path.Combine(declaringDirectory, path));
            source = NormalizePath(path);
            packageKey = CargoPackageKey.Path(manifest.ProjectScope, source, packageName);
        }
        else if (!string.IsNullOrWhiteSpace(git))
        {
            sourceKind = "git";
            source = NormalizeGitUrl(git);
            packageKey = CargoPackageKey.Git(source, packageName);
        }
        else
        {
            sourceKind = "registry";
            source = string.IsNullOrWhiteSpace(registry) ? "crates.io" : registry;
            packageKey = CargoPackageKey.Registry(source, packageName);
        }

        var gitReference = rev is not null ? $"rev:{rev}"
            : tag is not null ? $"tag:{tag}"
            : branch is not null ? $"branch:{branch}"
            : null;

        return new DependencySpecification(
            packageName,
            version,
            packageKey,
            sourceKind,
            source,
            workspaceInherited,
            optional,
            git,
            registry,
            path,
            branch,
            tag,
            rev,
            gitReference,
            resolvedPath,
            TargetQualifiedName: null,
            manifest.ProjectScope);
    }

    private static TomlTable? ReadDependencyTable(TomlTable? parent, string name) =>
        GetTable(parent, name);

    private static TomlTable? GetTable(TomlTable? parent, string name)
    {
        if (parent is null || !parent.TryGetValue(name, out var value))
            return null;
        return value as TomlTable;
    }

    private static string? GetString(TomlTable? table, string name)
    {
        if (table is null || !table.TryGetValue(name, out var value))
            return null;
        return value as string;
    }

    private static bool? GetBool(TomlTable? table, string name)
    {
        if (table is null || !table.TryGetValue(name, out var value))
            return null;
        return value as bool?;
    }

    private static object? ReadInheritedValue(TomlTable package, TomlTable? workspacePackage, string name)
    {
        if (!package.TryGetValue(name, out var local))
            return null;
        if (local is TomlTable inheritance && GetBool(inheritance, "workspace") == true)
            return workspacePackage is not null && workspacePackage.TryGetValue(name, out var inherited)
                ? inherited
                : null;
        return local;
    }

    private static string? ReadInheritedString(TomlTable package, TomlTable? workspacePackage, string name) =>
        ReadInheritedValue(package, workspacePackage, name) as string;

    private static bool IsWithinDirectory(string candidate, string parent)
    {
        if (string.IsNullOrEmpty(parent))
            return true;
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." ||
               (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
    }

    private static string NormalizeGitUrl(string value) =>
        value.Trim().TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value.Trim().TrimEnd('/')[..^4]
            : value.Trim().TrimEnd('/');

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static void AddEdge(
        List<PendingEdge> edges,
        HashSet<string> edgeKeys,
        PendingEdge edge)
    {
        var key = $"{edge.SourceQN}\n{edge.TargetQN}\n{edge.Type}";
        if (edgeKeys.Add(key))
            edges.Add(edge);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record ParsedManifest(
        string FullPath,
        string RelativePath,
        TomlTable Model,
        TomlTable? Package,
        TomlTable? Workspace)
    {
        public string ProjectScope { get; init; } = Path.GetFileName(
            Path.GetDirectoryName(FullPath) ?? FullPath);
    }

    private sealed record PackageDefinition(GraphNode Node);

    private sealed record DependencyReference(
        string TargetQualifiedName,
        GraphNode TargetNode,
        Dictionary<string, object> EdgeProperties);

    private sealed record DependencySpecification(
        string PackageName,
        string? Version,
        string PackageKey,
        string SourceKind,
        string Source,
        bool WorkspaceInherited,
        bool Optional,
        string? Git,
        string? Registry,
        string? Path,
        string? Branch,
        string? Tag,
        string? Rev,
        string? GitReference,
        string? ResolvedPath,
        string? TargetQualifiedName,
        string ProjectScope);

    private static class CargoPackageKey
    {
        public static string Registry(string registry, string name) =>
            $"cargo:registry:{registry.ToLowerInvariant()}:{name.ToLowerInvariant()}";

        public static string Git(string git, string name) =>
            $"cargo:git:{git.ToLowerInvariant()}:{name.ToLowerInvariant()}";

        public static string Path(string project, string path, string name) =>
            $"cargo:path:{project.ToLowerInvariant()}:{path.ToLowerInvariant()}:{name.ToLowerInvariant()}";

        public static string Workspace(string project, string name) =>
            $"cargo:workspace:{project.ToLowerInvariant()}:{name.ToLowerInvariant()}";

        public static string QualifiedName(string packageKey, string? version) =>
            $"package:{packageKey}@{(string.IsNullOrWhiteSpace(version) ? "*" : version)}";
    }
}
