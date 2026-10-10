using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Extractors.Rust;

/// <summary>
/// The parts of <c>cargo metadata --format-version 1</c> needed to split a Cargo workspace into
/// crate groups and describe one group to rust-analyzer as a <c>rust-project.json</c>.
/// </summary>
internal sealed class RustCargoWorkspace
{
    private static readonly HashSet<string> LibraryKinds = new(StringComparer.Ordinal)
    {
        "lib", "rlib", "dylib", "cdylib", "staticlib", "proc-macro"
    };

    private RustCargoWorkspace(
        IReadOnlyDictionary<string, CargoPackage> packages,
        IReadOnlyDictionary<string, CargoResolveNode> resolve,
        IReadOnlyList<string> workspaceMembers,
        string workspaceRoot)
    {
        Packages = packages;
        Resolve = resolve;
        WorkspaceMembers = workspaceMembers;
        WorkspaceRoot = workspaceRoot;
    }

    public IReadOnlyDictionary<string, CargoPackage> Packages { get; }
    public IReadOnlyDictionary<string, CargoResolveNode> Resolve { get; }
    public IReadOnlyList<string> WorkspaceMembers { get; }
    public string WorkspaceRoot { get; }

    public static RustCargoWorkspace Parse(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;

        var packages = new Dictionary<string, CargoPackage>(StringComparer.Ordinal);
        foreach (var package in root.GetProperty("packages").EnumerateArray())
        {
            var targets = package.GetProperty("targets").EnumerateArray()
                .Select(target => new CargoTarget(
                    target.GetProperty("name").GetString()!,
                    target.GetProperty("kind").EnumerateArray().Select(kind => kind.GetString()!).ToArray(),
                    target.GetProperty("src_path").GetString()!,
                    GetString(target, "edition") ?? GetString(package, "edition") ?? "2015"))
                .ToArray();
            var id = package.GetProperty("id").GetString()!;
            packages[id] = new CargoPackage(
                id,
                package.GetProperty("name").GetString()!,
                package.GetProperty("version").GetString()!,
                package.GetProperty("manifest_path").GetString()!,
                GetString(package, "source"),
                GetString(package, "repository"),
                targets);
        }

        var resolve = new Dictionary<string, CargoResolveNode>(StringComparer.Ordinal);
        if (root.TryGetProperty("resolve", out var resolveElement) &&
            resolveElement.ValueKind is JsonValueKind.Object)
        {
            foreach (var node in resolveElement.GetProperty("nodes").EnumerateArray())
            {
                var deps = node.TryGetProperty("deps", out var depsElement)
                    ? depsElement.EnumerateArray().Select(dep => new CargoDependency(
                            dep.GetProperty("name").GetString()!,
                            dep.GetProperty("pkg").GetString()!,
                            dep.TryGetProperty("dep_kinds", out var kinds)
                                ? kinds.EnumerateArray()
                                    .Select(kind => GetString(kind, "kind") ?? "normal")
                                    .ToArray()
                                : ["normal"]))
                        .ToArray()
                    : [];
                var features = node.TryGetProperty("features", out var featuresElement)
                    ? featuresElement.EnumerateArray().Select(feature => feature.GetString()!).ToArray()
                    : [];
                var id = node.GetProperty("id").GetString()!;
                resolve[id] = new CargoResolveNode(id, deps, features);
            }
        }

        var members = root.GetProperty("workspace_members").EnumerateArray()
            .Select(member => member.GetString()!)
            .Where(packages.ContainsKey)
            .ToArray();

        return new RustCargoWorkspace(
            packages,
            resolve,
            members,
            root.GetProperty("workspace_root").GetString()!);
    }

    /// <summary>
    /// Splits the workspace members into groups of at most <paramref name="groupSize"/> crates.
    /// Members are ordered by repository-relative package directory (ordinal), then package
    /// name, so a workspace always yields the same groups in the same order.
    /// </summary>
    public IReadOnlyList<RustCrateGroup> FormGroups(RustPathMapper paths, int groupSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupSize, 1);

        return WorkspaceMembers
            .Select(id => Packages[id])
            .Select(package => (Package: package, Directory: paths.ToRepositoryRelative(package.Directory)))
            .OrderBy(member => member.Directory, StringComparer.Ordinal)
            .ThenBy(member => member.Package.Name, StringComparer.Ordinal)
            .Chunk(groupSize)
            .Select((chunk, index) => new RustCrateGroup(
                index,
                chunk.Select(member => member.Package.Id).ToArray(),
                chunk.Select(member => member.Package.Name).ToArray(),
                chunk.Select(member => member.Directory).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Describes the crate graph to rust-analyzer with only <paramref name="group"/>'s crates as
    /// workspace members. Every other crate (other workspace members, dependencies) is a
    /// library crate: it is loaded for name resolution, but rust-analyzer neither type-checks
    /// its bodies for the index nor emits SCIP documents for it. Symbols keep Cargo's identity
    /// (package name and version), so a reference into another group's crate carries the same
    /// SCIP symbol that crate's own group defines. Build-script outputs and proc-macro dylibs
    /// appear only when <paramref name="buildOutputs"/> provides them; rust-analyzer never runs
    /// build scripts for a rust-project.json workspace.
    /// </summary>
    public string BuildProjectJson(
        RustCrateGroup group,
        RustPathMapper paths,
        string sysroot,
        RustBuildOutputs? buildOutputs)
    {
        var members = group.PackageIds.ToHashSet(StringComparer.Ordinal);
        var packageIds = Resolve.Count > 0
            ? Resolve.Keys.Where(Packages.ContainsKey)
            : Packages.Keys;
        var orderedPackages = packageIds
            .Select(id => Packages[id])
            .OrderBy(package => members.Contains(package.Id) ? 0 : 1)
            .ThenBy(package => package.Id, StringComparer.Ordinal)
            .ToList();

        var crates = new List<(CargoPackage Package, CargoTarget Target, JsonObject Crate)>();
        var libraryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var package in orderedPackages)
        {
            var isMember = members.Contains(package.Id);
            var library = package.Targets.FirstOrDefault(IsLibrary);
            var targets = isMember
                ? package.Targets.OrderBy(target => IsLibrary(target) ? 0 : 1).ToArray()
                : library is null ? [] : [library];
            foreach (var target in targets)
            {
                if (IsLibrary(target))
                    libraryIndex[package.Id] = crates.Count;
                var features = Resolve.TryGetValue(package.Id, out var resolved) ? resolved.Features : [];
                crates.Add((package, target, CreateCrate(package, target, features, isMember, paths, buildOutputs)));
            }
        }

        foreach (var (package, target, crate) in crates)
        {
            var deps = new JsonArray();
            var isMember = members.Contains(package.Id);
            var isBuildScript = target.Kinds.Contains("custom-build");
            if (!IsLibrary(target) && !isBuildScript && libraryIndex.TryGetValue(package.Id, out var ownLibrary))
            {
                deps.Add(new JsonObject
                {
                    ["crate"] = ownLibrary,
                    ["name"] = ToCrateName(crates[ownLibrary].Target.Name)
                });
            }

            if (Resolve.TryGetValue(package.Id, out var node))
            {
                foreach (var dependency in node.Dependencies)
                {
                    if (!libraryIndex.TryGetValue(dependency.PackageId, out var dependencyIndex) ||
                        !IncludesDependency(dependency, isBuildScript, isMember && !IsLibrary(target)))
                    {
                        continue;
                    }

                    deps.Add(new JsonObject
                    {
                        ["crate"] = dependencyIndex,
                        ["name"] = dependency.Name
                    });
                }
            }

            crate["deps"] = deps;
        }

        var sysrootSource = Path.Combine(sysroot, "lib", "rustlib", "src", "rust", "library");

        // One crate per line keeps the file readable in diagnostics.
        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append("\"sysroot\":").Append(JsonValue.Create(sysroot).ToJsonString()).Append(",\n");
        builder.Append("\"sysroot_src\":").Append(JsonValue.Create(sysrootSource).ToJsonString()).Append(",\n");
        builder.Append("\"crates\":[\n");
        builder.AppendJoin(",\n", crates.Select(entry => entry.Crate.ToJsonString()));
        builder.Append("\n]}\n");
        return builder.ToString();
    }

    private static bool IncludesDependency(CargoDependency dependency, bool isBuildScript, bool includeDevDependencies) =>
        isBuildScript
            ? dependency.Kinds.Contains("build")
            : dependency.Kinds.Any(kind => kind == "normal" || (includeDevDependencies && kind == "dev"));

    private static JsonObject CreateCrate(
        CargoPackage package,
        CargoTarget target,
        IReadOnlyList<string> features,
        bool isMember,
        RustPathMapper paths,
        RustBuildOutputs? buildOutputs)
    {
        var packageDirectory = paths.ToAnalyzerPath(package.Directory);
        var cfg = new JsonArray();
        var env = new JsonObject
        {
            ["CARGO_PKG_NAME"] = package.Name,
            ["CARGO_PKG_VERSION"] = package.Version,
            ["CARGO_MANIFEST_DIR"] = packageDirectory,
            ["CARGO_CRATE_NAME"] = ToCrateName(target.Name)
        };
        AddVersionParts(env, package.Version);

        var isBuildScript = target.Kinds.Contains("custom-build");
        // Cargo's resolved features for the package; build scripts see them through env only.
        if (!isBuildScript)
        {
            foreach (var feature in features)
                cfg.Add($"feature=\"{feature}\"");
        }

        if (!isBuildScript && buildOutputs?.For(package.Id) is { } output)
        {
            foreach (var flag in output.Cfgs)
                cfg.Add(flag);
            foreach (var (key, value) in output.Env)
                env[key] = value;
            if (output.OutDir is not null)
                env["OUT_DIR"] = paths.ToAnalyzerPath(output.OutDir);
        }

        var crate = new JsonObject
        {
            ["display_name"] = package.Name,
            ["root_module"] = paths.ToAnalyzerPath(target.SourcePath),
            ["edition"] = target.Edition,
            ["version"] = package.Version,
            ["cfg"] = cfg,
            ["env"] = env,
            ["is_workspace_member"] = isMember,
            ["is_proc_macro"] = target.Kinds.Contains("proc-macro"),
            ["source"] = new JsonObject
            {
                ["include_dirs"] = new JsonArray(packageDirectory),
                ["exclude_dirs"] = paths.IsInRepository(package.Directory)
                    ? new JsonArray(
                        Path.Combine(packageDirectory, "target"),
                        Path.Combine(packageDirectory, ".git"))
                    : new JsonArray()
            }
        };
        if (!string.IsNullOrWhiteSpace(package.Repository))
            crate["repository"] = package.Repository;
        if (target.Kinds.Contains("proc-macro") && buildOutputs?.ProcMacroDylib(package.Id) is { } dylib)
            crate["proc_macro_dylib_path"] = dylib;
        return crate;
    }

    private static void AddVersionParts(JsonObject env, string version)
    {
        var core = version.Split('+', 2)[0];
        var preSplit = core.Split('-', 2);
        var parts = preSplit[0].Split('.');
        env["CARGO_PKG_VERSION_MAJOR"] = parts.ElementAtOrDefault(0) ?? "0";
        env["CARGO_PKG_VERSION_MINOR"] = parts.ElementAtOrDefault(1) ?? "0";
        env["CARGO_PKG_VERSION_PATCH"] = parts.ElementAtOrDefault(2) ?? "0";
        env["CARGO_PKG_VERSION_PRE"] = preSplit.Length > 1 ? preSplit[1] : "";
    }

    private static bool IsLibrary(CargoTarget target) => target.Kinds.Any(LibraryKinds.Contains);

    private static string ToCrateName(string targetName) => targetName.Replace('-', '_');

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed record CargoPackage(
    string Id,
    string Name,
    string Version,
    string ManifestPath,
    string? Source,
    string? Repository,
    IReadOnlyList<CargoTarget> Targets)
{
    public string Directory => Path.GetDirectoryName(ManifestPath) ?? ManifestPath;
}

internal sealed record CargoTarget(string Name, IReadOnlyList<string> Kinds, string SourcePath, string Edition);

internal sealed record CargoDependency(string Name, string PackageId, IReadOnlyList<string> Kinds);

internal sealed record CargoResolveNode(
    string Id,
    IReadOnlyList<CargoDependency> Dependencies,
    IReadOnlyList<string> Features);

/// <summary>A deterministic slice of a Cargo workspace's members.</summary>
internal sealed record RustCrateGroup(
    int Index,
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> Crates,
    IReadOnlyList<string> SourceDirectories);

/// <summary>
/// Maps paths between the real checkout and the analyzer view of it. rust-analyzer reads the
/// group's rust-project.json from a scratch directory whose entries are symlinks to the
/// checkout's top-level entries, so the checkout itself is never written to and SCIP document
/// paths stay repository-relative. Cargo may report paths through symlinks the configured
/// checkout path does not use (or the reverse, e.g. macOS /var and /private/var), so both
/// sides are compared after resolving symlinks.
/// </summary>
internal sealed class RustPathMapper
{
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);
    private readonly string _repositoryRoot;
    private readonly string _analyzerRoot;

    public RustPathMapper(string repositoryRoot, string analyzerRoot)
    {
        _repositoryRoot = Path.TrimEndingDirectorySeparator(Canonicalize(repositoryRoot));
        _analyzerRoot = Path.TrimEndingDirectorySeparator(analyzerRoot);
    }

    public string RepositoryRoot => _repositoryRoot;

    public bool IsInRepository(string path) => IsUnderRepository(Canonicalize(path));

    public string ToAnalyzerPath(string path)
    {
        var canonical = Canonicalize(path);
        return IsUnderRepository(canonical) ? _analyzerRoot + canonical[_repositoryRoot.Length..] : path;
    }

    public string ToRepositoryRelative(string path)
    {
        var relative = Path.GetRelativePath(_repositoryRoot, Canonicalize(path)).Replace('\\', '/');
        return relative == "." ? "" : relative.Trim('/');
    }

    private bool IsUnderRepository(string canonical) =>
        canonical == _repositoryRoot ||
        canonical.StartsWith(_repositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Resolves every symlink along <paramref name="path"/>; missing parts stay as written.</summary>
    private string Canonicalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (_canonical.TryGetValue(full, out var cached))
            return cached;

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (_canonical.TryGetValue(current, out var resolved))
            {
                current = resolved;
                continue;
            }

            var key = current;
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null &&
                    info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Canonicalize(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unresolvable links are compared as written.
            }

            _canonical[key] = current;
        }

        _canonical[full] = current;
        return current;
    }
}

/// <summary>Outputs of <c>cargo check --message-format=json</c> for one crate group.</summary>
internal sealed class RustBuildOutputs
{
    private readonly Dictionary<string, BuildScriptOutput> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _procMacroDylibs = new(StringComparer.Ordinal);
    private readonly bool _includeProcMacros;

    public RustBuildOutputs(bool includeProcMacros)
    {
        _includeProcMacros = includeProcMacros;
    }

    public BuildScriptOutput? For(string packageId) => _outputs.GetValueOrDefault(packageId);

    public string? ProcMacroDylib(string packageId) =>
        _includeProcMacros ? _procMacroDylibs.GetValueOrDefault(packageId) : null;

    public void AddCargoMessages(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var message = document.RootElement;
                var reason = message.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : null;
                var packageId = message.TryGetProperty("package_id", out var packageElement)
                    ? packageElement.GetString()
                    : null;
                if (packageId is null)
                    continue;

                if (reason == "build-script-executed")
                {
                    var cfgs = message.TryGetProperty("cfgs", out var cfgElement)
                        ? cfgElement.EnumerateArray().Select(cfg => cfg.GetString()!).ToArray()
                        : [];
                    var env = message.TryGetProperty("env", out var envElement)
                        ? envElement.EnumerateArray()
                            .Select(pair => pair.EnumerateArray().Select(part => part.GetString() ?? "").ToArray())
                            .Where(pair => pair.Length == 2)
                            .Select(pair => (pair[0], pair[1]))
                            .ToArray()
                        : [];
                    var outDir = message.TryGetProperty("out_dir", out var outDirElement)
                        ? outDirElement.GetString()
                        : null;
                    _outputs[packageId] = new BuildScriptOutput(
                        cfgs,
                        env,
                        string.IsNullOrWhiteSpace(outDir) ? null : outDir);
                }
                else if (reason == "compiler-artifact" &&
                         message.TryGetProperty("target", out var target) &&
                         target.TryGetProperty("kind", out var kinds) &&
                         kinds.EnumerateArray().Any(kind => kind.GetString() == "proc-macro") &&
                         message.TryGetProperty("filenames", out var filenames))
                {
                    var dylib = filenames.EnumerateArray()
                        .Select(file => file.GetString())
                        .FirstOrDefault(file => file is not null &&
                                                (file.EndsWith(".so", StringComparison.Ordinal) ||
                                                 file.EndsWith(".dylib", StringComparison.Ordinal) ||
                                                 file.EndsWith(".dll", StringComparison.Ordinal)));
                    if (dylib is not null)
                        _procMacroDylibs[packageId] = dylib;
                }
            }
            catch (JsonException)
            {
                // cargo interleaves non-JSON diagnostics; only its JSON messages matter.
            }
        }
    }
}

internal sealed record BuildScriptOutput(
    IReadOnlyList<string> Cfgs,
    IReadOnlyList<(string Key, string Value)> Env,
    string? OutDir);
