using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>
    /// The Swift module scopes of the repository for call resolution: the SwiftPM targets and Xcode
    /// projects of this run's buffer, and, when this run did not extract the manifests (an overlay),
    /// the persisted ones.
    /// </summary>
    private async Task<SwiftModuleScopes> LoadSwiftModuleScopesAsync(
        string projectName,
        GraphBuffer buffer,
        bool swiftManifestsExtracted,
        IReadOnlySet<string> excludedPersistedFiles)
    {
        var modules = buffer.AllNodes.Where(IsSwiftModuleProvider).Where(node => node.Label == NodeLabel.Module);
        if (!swiftManifestsExtracted)
        {
            var persisted = (await _store.FindNodesByLabelAsync(projectName, NodeLabel.Module, int.MaxValue))
                .Where(IsSwiftModuleProvider)
                .Where(node => !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath ?? "")));
            modules = modules.Concat(persisted);
        }

        var moduleList = modules.DistinctBy(node => node.QualifiedName, StringComparer.Ordinal).ToList();
        var imports = buffer.AllPendingEdges
            .Where(edge => edge.Type == EdgeType.IMPORTS
                           && PlainString(edge.Properties, SwiftImportProperty) == "module"
                           && PlainString(edge.Properties, "matched_by") == SwiftModuleResolver.MatchedByTarget);
        return new SwiftModuleScopes(moduleList, imports);
    }

    /// <summary>
    /// Which Swift module each Swift file belongs to, and which in-repository modules it imports,
    /// so a Swift call binds only to code its file can see (sc-26145: a vendored copy of an app
    /// target must not call into the original's extensions).
    /// </summary>
    /// <remarks>
    /// A file belongs to the module whose source directory is its nearest ancestor: a SwiftPM
    /// target's <c>source_path</c>, else the directory of an Xcode project (all native targets of
    /// one project form one scope, since the project file's group membership is not read). A
    /// SwiftPM target wins over an Xcode project at the same depth. A file under no module, and a
    /// candidate under no module, are not filtered.
    /// </remarks>
    internal sealed class SwiftModuleScopes
    {
        private readonly List<(string Directory, string Scope, bool IsXcode)> _roots = [];
        private readonly Dictionary<string, string> _scopeByModule = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _importsByFile = new(StringComparer.Ordinal);

        public SwiftModuleScopes(IEnumerable<GraphNode> modules, IEnumerable<PendingEdge> moduleImports)
        {
            foreach (var module in modules)
            {
                var isXcode = PlainString(module.Properties, "ecosystem") == SwiftPackageManifestExtractor.XcodeEcosystem;
                var directory = isXcode
                    ? PlainString(module.Properties, SwiftPackageManifestExtractor.ProjectDirectoryProperty)
                    : PlainString(module.Properties, "source_path");
                if (directory is null)
                    continue;

                var scope = isXcode
                    ? "xcode:" + (PlainString(module.Properties, "manifest_path") ?? module.FilePath ?? "")
                    : module.QualifiedName;
                _scopeByModule[module.QualifiedName] = scope;
                _roots.Add((NormalizeRelativePath(directory).Trim('/'), scope, isXcode));
            }

            foreach (var edge in moduleImports)
            {
                if (!_scopeByModule.TryGetValue(edge.TargetQN, out var scope))
                    continue;
                if (!_importsByFile.TryGetValue(edge.SourceQN, out var scopes))
                    _importsByFile[edge.SourceQN] = scopes = new HashSet<string>(StringComparer.Ordinal);
                scopes.Add(scope);
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
            foreach (var (directory, scope, isXcode) in _roots)
            {
                if (directory.Length > 0 && !file.StartsWith(directory + "/", StringComparison.Ordinal))
                    continue;

                var depth = directory.Length == 0 ? 0 : directory.Count(ch => ch == '/') + 1;
                if (best is null || depth > best.Value.Depth || depth == best.Value.Depth && best.Value.IsXcode && !isXcode)
                    best = (depth, isXcode, scope);
            }

            return best?.Scope;
        }

        /// <summary>
        /// Whether a Swift file (<paramref name="callerFileQN"/>, in <paramref name="callerScope"/>)
        /// can see the Swift definition declared in <paramref name="candidateFilePath"/>: it is in
        /// the caller's own module or in a module the caller's file imports.
        /// </summary>
        public bool IsVisible(string? callerScope, string callerFileQN, string? candidateFilePath)
        {
            if (callerScope is null)
                return true;

            var candidateScope = ScopeOf(candidateFilePath);
            return candidateScope is null
                   || candidateScope == callerScope
                   || _importsByFile.TryGetValue(callerFileQN, out var imported) && imported.Contains(candidateScope);
        }
    }
}
