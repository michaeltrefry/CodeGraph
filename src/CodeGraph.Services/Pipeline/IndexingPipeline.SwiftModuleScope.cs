using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>
    /// The Swift module scopes of the repository for call resolution: the SwiftPM targets and Xcode
    /// repositories of this run's buffer (and, when this run did not extract the manifests, an overlay,
    /// the persisted ones), and every in-repository module import: this run's, plus, when the run
    /// writes a slice of a persisted graph, the persisted imports of files it did not re-extract
    /// (as re-bound by this run's import resolution), since re-exports live in those files.
    /// </summary>
    private async Task<SwiftModuleScopes> LoadSwiftModuleScopesAsync(
        string repositoryName,
        GraphBuffer buffer,
        bool swiftManifestsExtracted,
        bool includePersistedImports,
        IReadOnlySet<string> excludedPersistedFiles,
        SwiftRebinding importRebinding,
        CancellationToken ct)
    {
        var modules = buffer.AllNodes.Where(IsSwiftModuleProvider).Where(node => node.Label == NodeLabel.Module);
        if (!swiftManifestsExtracted)
        {
            var persisted = (await _store.FindNodesByLabelAsync(repositoryName, NodeLabel.Module, int.MaxValue))
                .Where(IsSwiftModuleProvider)
                .Where(node => !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath ?? "")));
            modules = modules.Concat(persisted);
        }

        var moduleList = modules.DistinctBy(node => node.QualifiedName, StringComparer.Ordinal).ToList();
        if (moduleList.Count == 0)
            return new SwiftModuleScopes([], []);

        IEnumerable<PendingEdge> imports = buffer.AllPendingEdges;
        if (includePersistedImports)
        {
            static string Key(PendingEdge edge) => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";
            var retracted = importRebinding.Retractions.Edges.Select(Key).ToHashSet(StringComparer.Ordinal);
            var persisted = (await _store.FindEdgesByPropertyAsync(
                    repositoryName, [EdgeType.IMPORTS], SwiftImportProperty, ["module"], ct))
                .Where(edge => !excludedPersistedFiles.Contains(NormalizeRelativePath(edge.SourceFilePath ?? "")))
                .Select(ToPendingEdge)
                .Where(edge => !retracted.Contains(Key(edge)));
            imports = imports.Concat(persisted).Concat(importRebinding.ReboundEdges);
        }

        var moduleImports = imports
            .Where(edge => edge.Type == EdgeType.IMPORTS
                           && PlainString(edge.Properties, SwiftImportProperty) == "module"
                           && PlainString(edge.Properties, "matched_by") == SwiftModuleResolver.MatchedByTarget)
            .Select(edge => new SwiftModuleImport(
                edge.SourceQN, edge.TargetQN, PlainBool(edge.Properties, ExportedProperty)));
        return new SwiftModuleScopes(moduleList, moduleImports, repositoryName);
    }

    internal sealed record SwiftModuleImport(string FileQN, string ModuleQN, bool Exported);

    /// <summary>
    /// Which Swift module each Swift file belongs to, and which in-repository modules it sees
    /// through its imports, so an ambiguous Swift call prefers the candidate its file can see
    /// (sc-26145: a vendored copy of an app target must not bind into the original's extensions).
    /// </summary>
    /// <remarks>
    /// A file belongs to the module whose source root is its nearest ancestor (or the file itself):
    /// each <c>sources:</c> subpath of a SwiftPM target, else its <c>source_path</c>, else the
    /// directory of an Xcode project (all native targets of one project form one scope, since the
    /// project file's group membership is not read). SwiftPM targets that share a root without
    /// <c>sources:</c> form one scope. A SwiftPM target wins over an Xcode project at the same depth.
    /// A file sees the modules it imports and, transitively, the modules those re-export
    /// (<c>@_exported import</c> in any file of the imported module).
    /// </remarks>
    internal sealed class SwiftModuleScopes
    {
        private readonly List<(string Root, string Scope, bool IsXcode)> _roots = [];
        private readonly Dictionary<string, string> _scopeByModule = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _importsByFile = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _reexportsByScope = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlySet<string>> _visibleByFile = new(StringComparer.Ordinal);

        public SwiftModuleScopes(IEnumerable<GraphNode> modules, IEnumerable<SwiftModuleImport> imports, string repositoryName = "")
        {
            // Roots of each module; SwiftPM modules sharing a root are one scope (union by root).
            var rootsByModule = new List<(GraphNode Module, bool IsXcode, List<string> Roots)>();
            foreach (var module in modules)
            {
                var isXcode = PlainString(module.Properties, "ecosystem") == SwiftPackageManifestExtractor.XcodeEcosystem;
                var roots = isXcode
                    ? [PlainString(module.Properties, SwiftPackageManifestExtractor.ProjectDirectoryProperty)]
                    : PlainString(module.Properties, SwiftPackageManifestExtractor.SourcePathsProperty) is { Length: > 0 } sources
                        ? sources.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(source => (string?)source).ToList()
                        : [PlainString(module.Properties, "source_path")];
                var normalized = roots.OfType<string>().Select(root => NormalizeRelativePath(root)).Distinct().ToList();
                if (normalized.Count > 0)
                    rootsByModule.Add((module, isXcode, normalized));
            }

            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            string Find(string key)
            {
                while (parent.TryGetValue(key, out var next) && next != key)
                    key = next;
                return key;
            }

            void Union(string left, string right)
            {
                var (a, b) = (Find(left), Find(right));
                if (a == b)
                    return;
                if (string.CompareOrdinal(a, b) < 0)
                    parent[b] = a;
                else
                    parent[a] = b;
            }

            string KeyOf(GraphNode module, bool isXcode) => isXcode
                ? "xcode:" + (PlainString(module.Properties, "manifest_path") ?? module.FilePath ?? "")
                : module.QualifiedName;

            foreach (var group in rootsByModule.Where(entry => !entry.IsXcode)
                         .SelectMany(entry => entry.Roots.Select(root => (Root: root, Key: KeyOf(entry.Module, false))))
                         .GroupBy(entry => entry.Root, StringComparer.Ordinal))
            {
                var keys = group.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).ToList();
                foreach (var key in keys.Skip(1))
                    Union(keys[0], key);
            }

            foreach (var (module, isXcode, roots) in rootsByModule)
            {
                var scope = Find(KeyOf(module, isXcode));
                _scopeByModule[module.QualifiedName] = scope;
                foreach (var root in roots)
                    _roots.Add((root, scope, isXcode));
            }

            var filePrefix = repositoryName.Length > 0 ? repositoryName + ":" : null;
            foreach (var import in imports)
            {
                if (!_scopeByModule.TryGetValue(import.ModuleQN, out var scope))
                    continue;
                if (!_importsByFile.TryGetValue(import.FileQN, out var scopes))
                    _importsByFile[import.FileQN] = scopes = new HashSet<string>(StringComparer.Ordinal);
                scopes.Add(scope);

                if (!import.Exported || filePrefix is null || !import.FileQN.StartsWith(filePrefix, StringComparison.Ordinal))
                    continue;
                if (ScopeOf(import.FileQN[filePrefix.Length..]) is not { } exporter || exporter == scope)
                    continue;
                if (!_reexportsByScope.TryGetValue(exporter, out var reexports))
                    _reexportsByScope[exporter] = reexports = new HashSet<string>(StringComparer.Ordinal);
                reexports.Add(scope);
            }
        }

        public bool IsEmpty => _roots.Count == 0;

        /// <summary>The module scope a repository-relative Swift file belongs to, or null.</summary>
        public string? ScopeOf(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var file = NormalizeRelativePath(filePath);
            (int Depth, bool IsXcode, string Scope)? best = null;
            foreach (var (root, scope, isXcode) in _roots)
            {
                if (root.Length > 0 && file != root && !file.StartsWith(root + "/", StringComparison.Ordinal))
                    continue;

                var depth = root.Length == 0 ? 0 : root.Count(ch => ch == '/') + 1;
                if (best is null || depth > best.Value.Depth || depth == best.Value.Depth && best.Value.IsXcode && !isXcode)
                    best = (depth, isXcode, scope);
            }

            return best?.Scope;
        }

        /// <summary>
        /// The module scopes a Swift file sees through its imports: the modules it imports and,
        /// transitively, the modules they re-export.
        /// </summary>
        public IReadOnlySet<string> ImportedScopesOf(string fileQN)
        {
            if (_visibleByFile.TryGetValue(fileQN, out var cached))
                return cached;

            var visible = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(_importsByFile.GetValueOrDefault(fileQN) ?? []);
            while (pending.Count > 0)
            {
                var scope = pending.Pop();
                if (!visible.Add(scope))
                    continue;
                foreach (var reexported in _reexportsByScope.GetValueOrDefault(scope) ?? [])
                    pending.Push(reexported);
            }

            _visibleByFile[fileQN] = visible;
            return visible;
        }

        /// <summary>
        /// Whether a Swift file (<paramref name="callerFileQN"/>, in <paramref name="callerScope"/>)
        /// can see the definition declared in <paramref name="candidateFilePath"/>: it is in the
        /// caller's own module, in a module the caller sees through its imports, or in no module.
        /// </summary>
        public bool IsVisible(string? callerScope, string callerFileQN, string? candidateFilePath)
        {
            if (callerScope is null)
                return true;

            var candidateScope = ScopeOf(candidateFilePath);
            return candidateScope is null
                   || candidateScope == callerScope
                   || ImportedScopesOf(callerFileQN).Contains(candidateScope);
        }
    }
}
