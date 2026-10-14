using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Resolution of Swift <c>import</c> declarations to the modules that provide them.
/// </summary>
/// <remarks>
/// <para>
/// <c>import M</c> becomes an IMPORTS edge from the importing file to, in order: the SwiftPM target
/// named <c>M</c> declared in the repository (a Module node of a <c>Package.swift</c>) or the Xcode
/// native target that builds module <c>M</c> (a Module node of a <c>project.pbxproj</c>, nearness
/// measured from the directory holding the <c>.xcodeproj</c>); the declared
/// dependency whose product <c>M</c> one of the repository's targets uses
/// (<c>.product(name: "M", package: ...)</c>); a declared, else pinned, dependency whose identity or
/// name is <c>M</c>. When several candidates remain, the one whose manifest is the nearest ancestor of
/// the importing file wins; a remaining tie binds to every candidate with low confidence. A module
/// nothing in the repository provides (Foundation, SwiftUI, UIKit, AppKit, Combine, a dependency
/// without a manifest) is external and binds to a <see cref="ExternalModulePrefix"/> stub, the module
/// counterpart of the <see cref="SwiftTypeReference.ExternalPrefix"/> type stubs.
/// </para>
/// <para>
/// A scoped import of a type (<c>import struct M.T</c>) from an in-repository target also links the
/// file to the type, through a <see cref="SwiftTypeReference"/> the type resolution binds.
/// </para>
/// <para>
/// Every module edge records the module it imports (<see cref="ModuleProperty"/>), so an incremental
/// run whose manifests changed re-resolves the persisted imports of files it does not re-extract:
/// an import keeps pointing at the module's current provider (a re-pinned version, a new target, a
/// removed dependency) without re-extracting the importing file.
/// </para>
/// </remarks>
public partial class IndexingPipeline
{
    /// <summary>Prefix of the stub node of a module declared outside the repository.</summary>
    internal const string ExternalModulePrefix = "swift-external-module:";

    /// <summary>Marks an IMPORTS edge of a Swift module import (value <c>module</c>).</summary>
    internal const string SwiftImportProperty = "swift_import";

    /// <summary>The imported top-level module (<c>Darwin</c> for <c>import Darwin.C</c>).</summary>
    internal const string ModuleProperty = "swift_module";

    private const string SwiftPmEcosystem = "swiftpm";

    private static readonly HashSet<string> ScopedTypeImportKinds = new(StringComparer.Ordinal)
    {
        "struct", "class", "enum", "protocol"
    };

    internal static bool IsExternalModuleStub(string? qualifiedName) =>
        qualifiedName?.StartsWith(ExternalModulePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Resolves this run's Swift imports and, with <paramref name="rebindPersisted"/>, re-resolves
    /// the persisted module imports of files this run does not re-extract.
    /// </summary>
    /// <param name="manifestsExtracted">
    /// The buffer holds every manifest's packages and targets (a default-branch run); otherwise
    /// (an overlay) the providers are read from the persisted graph.
    /// </param>
    /// <param name="incremental">A default-branch run that writes a file slice of the persisted graph.</param>
    internal async Task<SwiftRebinding> ResolveSwiftImportsAsync(
        string projectName,
        GraphBuffer buffer,
        bool manifestsExtracted,
        bool incremental,
        bool rebindPersisted,
        IReadOnlySet<string> excludedPersistedFiles,
        CancellationToken ct)
    {
        var imports = buffer.AllUnresolvedImports
            .Where(import => import.FileQN.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var swiftFilesReplaced = incremental && excludedPersistedFiles.Any(file =>
            file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase));
        if (imports.Count == 0 && !rebindPersisted && !swiftFilesReplaced)
            return SwiftRebinding.None;

        var providers = buffer.AllNodes.Where(IsSwiftModuleProvider).ToList();
        if (!manifestsExtracted)
        {
            var persisted = (await _store.FindNodesByLabelAsync(projectName, NodeLabel.Module, int.MaxValue))
                .Concat(await _store.FindNodesByLabelAsync(projectName, NodeLabel.Package, int.MaxValue))
                .Where(IsSwiftModuleProvider)
                .Where(node => !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath ?? "")));
            providers = providers.Concat(persisted)
                .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
                .ToList();
        }

        var resolver = new SwiftModuleResolver(providers);
        var stubs = new Dictionary<string, string>(StringComparer.Ordinal);

        List<PendingEdge> ResolveModule(string fileQN, string filePath, string module, string? symbols)
        {
            var resolution = resolver.Resolve(module, filePath);
            var edges = new List<PendingEdge>();
            IEnumerable<string> targets = resolution.Targets.Select(node => node.QualifiedName);
            if (resolution.Targets.Count == 0)
            {
                var stubQN = ExternalModulePrefix + module;
                stubs[stubQN] = module;
                targets = [stubQN];
            }

            foreach (var target in targets)
            {
                var properties = new Dictionary<string, object>
                {
                    [SwiftImportProperty] = "module",
                    [ModuleProperty] = module,
                    ["matched_by"] = resolution.MatchedBy,
                    ["confidence"] = resolution.Ambiguous ? "low" : "high"
                };
                if (symbols is not null)
                    properties["imported_symbols"] = symbols;
                edges.Add(new PendingEdge(fileQN, target, EdgeType.IMPORTS, properties));
            }

            return edges;
        }

        // One module edge per file and top-level module (`import M`, `import struct M.T` and
        // `import M.Sub` all import module M); the scoped symbols are listed on it.
        var filePrefixLength = projectName.Length + 1;
        foreach (var group in imports.GroupBy(import =>
                     (import.FileQN, Module: SwiftModuleResolver.TopLevelModule(import.ImportedNamespace))))
        {
            ct.ThrowIfCancellationRequested();
            var (fileQN, module) = group.Key;
            var filePath = fileQN[filePrefixLength..];
            var symbols = group.Select(import => import.ImportedSymbol).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var moduleEdges = ResolveModule(fileQN, filePath, module, symbols.Count == 0 ? null : string.Join(',', symbols));
            foreach (var edge in moduleEdges)
                buffer.AddEdge(edge);
            if (!moduleEdges.Any(edge => PlainString(edge.Properties, "matched_by") == SwiftModuleResolver.MatchedByTarget))
                continue;

            // `import struct M.T` of an in-repository module also imports the type T.
            foreach (var import in group.Where(import => import.ImportedSymbol is not null
                         && import.ImportedSymbolKind is { } kind && ScopedTypeImportKinds.Contains(kind)))
            {
                var properties = new Dictionary<string, object>
                {
                    [SwiftTypeReference.OriginFileProperty] = filePath,
                    [ModuleProperty] = module,
                    ["imported_symbols"] = import.ImportedSymbol!,
                    ["confidence"] = "high"
                };
                if (import.ImportedSymbolKind == "protocol")
                    properties[SwiftTypeReference.ProtocolTargetProperty] = true;
                buffer.AddEdge(new PendingEdge(
                    fileQN, SwiftTypeReference.Create(import.ImportedSymbol!), EdgeType.IMPORTS, properties));
            }
        }

        var rebound = new List<PendingEdge>();
        var retracted = new List<PendingEdge>();
        if (rebindPersisted)
        {
            var stored = await _store.FindEdgesByPropertyAsync(
                projectName, [EdgeType.IMPORTS], SwiftImportProperty, ["module"], ct);
            // One import can bind to several candidates; it is re-resolved once.
            foreach (var group in stored
                         .Where(edge => !excludedPersistedFiles.Contains(NormalizeRelativePath(edge.SourceFilePath ?? "")))
                         .GroupBy(edge => (edge.SourceQN, Module: PlainString(edge.Properties, ModuleProperty)))
                         .Where(group => group.Key.Module is not null))
            {
                ct.ThrowIfCancellationRequested();
                var first = group.First();
                var resolved = ResolveModule(
                    first.SourceQN,
                    NormalizeRelativePath(first.SourceFilePath ?? ""),
                    group.Key.Module!,
                    PlainString(first.Properties, "imported_symbols"));
                var storedBindings = group
                    .Select(edge => $"{edge.TargetQN}\u001f{PlainString(edge.Properties, "matched_by")}\u001f{PlainString(edge.Properties, "confidence")}")
                    .ToHashSet(StringComparer.Ordinal);
                var resolvedBindings = resolved
                    .Select(edge => $"{edge.TargetQN}\u001f{PlainString(edge.Properties, "matched_by")}\u001f{PlainString(edge.Properties, "confidence")}")
                    .ToHashSet(StringComparer.Ordinal);
                if (storedBindings.SetEquals(resolvedBindings))
                    continue;

                retracted.AddRange(group.Select(ToPendingEdge));
                rebound.AddRange(resolved);
            }

            foreach (var edge in rebound)
                buffer.AddEdge(edge);
        }

        foreach (var (qualifiedName, name) in stubs)
        {
            if (buffer.FindByQN(qualifiedName) is not null)
                continue;
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.Module,
                Name = name,
                QualifiedName = qualifiedName,
                Properties = new()
                {
                    ["stub"] = true,
                    ["external"] = true,
                    ["language"] = "Swift",
                    [SwiftPackageManifestExtractor.ModuleNameProperty] = name
                }
            });
        }

        // External module stubs left without any import (their importers were re-extracted,
        // deleted or re-bound) are removed by the write.
        IReadOnlyList<string> orphanCandidates = [];
        if (incremental && (swiftFilesReplaced || retracted.Count > 0))
        {
            orphanCandidates = (await _store.FindNodesByLabelAsync(projectName, NodeLabel.Module, int.MaxValue))
                .Select(node => node.QualifiedName)
                .Where(IsExternalModuleStub)
                .ToList();
        }

        _logger.LogInformation(
            "Swift imports: {Imports} resolved, {Rebound} persisted import edge(s) re-bound, {Retracted} retracted, {Stubs} external module(s)",
            imports.Count, rebound.Count, retracted.Count, stubs.Count);

        return new SwiftRebinding(rebound, new GraphSliceRetractions(retracted, orphanCandidates));
    }

    private static bool IsSwiftModuleProvider(GraphNode node) =>
        (node.Label is NodeLabel.Module or NodeLabel.Package
         && PlainString(node.Properties, "ecosystem") == SwiftPmEcosystem)
        || (node.Label is NodeLabel.Module
            && PlainString(node.Properties, "ecosystem") == SwiftPackageManifestExtractor.XcodeEcosystem);

    /// <summary>Both re-bindings of one run, applied by the same write.</summary>
    private static SwiftRebinding Combine(SwiftRebinding first, SwiftRebinding second) =>
        new(
            first.ReboundEdges.Concat(second.ReboundEdges).ToList(),
            new GraphSliceRetractions(
                first.Retractions.Edges.Concat(second.Retractions.Edges).ToList(),
                first.Retractions.OrphanCandidateQNs.Concat(second.Retractions.OrphanCandidateQNs)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()));
}

/// <summary>
/// Picks the provider of a Swift module among the repository's SwiftPM targets and packages (see
/// <see cref="IndexingPipeline.ResolveSwiftImportsAsync"/>).
/// </summary>
internal sealed class SwiftModuleResolver
{
    public const string MatchedByTarget = "target";
    public const string MatchedByProduct = "product";
    public const string MatchedByIdentity = "identity";
    public const string MatchedByExternal = "external";

    private readonly ILookup<string, GraphNode> _targets;
    private readonly ILookup<string, GraphNode> _products;
    private readonly ILookup<string, GraphNode> _declaredByName;
    private readonly ILookup<string, GraphNode> _pinnedByName;

    public SwiftModuleResolver(IEnumerable<GraphNode> providers)
    {
        var list = providers.ToList();
        _targets = list.Where(node => node.Label == NodeLabel.Module)
            .ToLookup(node => Property(node, SwiftPackageManifestExtractor.ModuleNameProperty) ?? node.Name,
                StringComparer.Ordinal);
        var dependencies = list
            .Where(node => node.Label == NodeLabel.Package && Property(node, "is_definition") != "true")
            .ToList();
        var declared = dependencies.Where(node => Property(node, "dependency_kind") == "declared").ToList();
        _products = declared
            .SelectMany(node => (Property(node, SwiftPackageManifestExtractor.ProductsProperty) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(product => (Product: product, Node: node)))
            .ToLookup(entry => entry.Product, entry => entry.Node, StringComparer.Ordinal);
        _declaredByName = declared
            .SelectMany(node => Names(node).Select(name => (Name: name, Node: node)))
            .ToLookup(entry => entry.Name, entry => entry.Node, StringComparer.OrdinalIgnoreCase);
        _pinnedByName = dependencies.Where(node => Property(node, "dependency_kind") == "pinned")
            .SelectMany(node => Names(node).Select(name => (Name: name, Node: node)))
            .ToLookup(entry => entry.Name, entry => entry.Node, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The top-level module of a submodule import (<c>Darwin.C</c> → <c>Darwin</c>).</summary>
    public static string TopLevelModule(string module)
    {
        var dot = module.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? module : module[..dot];
    }

    public SwiftModuleResolution Resolve(string module, string importingFile)
    {
        var name = TopLevelModule(module);
        if (_targets[name].Any())
            return Nearest(_targets[name], importingFile, MatchedByTarget);
        if (_products[name].Any())
            return Nearest(_products[name], importingFile, MatchedByProduct);
        if (_declaredByName[name].Any())
            return Nearest(_declaredByName[name], importingFile, MatchedByIdentity);
        if (_pinnedByName[name].Any())
            return Nearest(_pinnedByName[name], importingFile, MatchedByIdentity);
        return new SwiftModuleResolution([], MatchedByExternal, Ambiguous: false);
    }

    /// <summary>
    /// The candidates whose manifest is the nearest ancestor directory of the importing file (all of
    /// them when none is an ancestor). Candidates of one package (same <c>package_key</c>) are one
    /// provider; distinct providers that remain are ambiguous.
    /// </summary>
    private static SwiftModuleResolution Nearest(IEnumerable<GraphNode> candidates, string importingFile, string matchedBy)
    {
        var ranked = candidates
            .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Select(node => (Node: node, Depth: AncestorDepth(ManifestDirectory(node), importingFile)))
            .ToList();
        var best = ranked.Max(entry => entry.Depth);
        var nearest = ranked.Where(entry => entry.Depth == best)
            .Select(entry => entry.Node)
            .OrderBy(node => node.QualifiedName, StringComparer.Ordinal)
            .ToList();
        var providers = nearest
            .GroupBy(node => Property(node, "package_key") ?? node.QualifiedName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return new SwiftModuleResolution(providers, matchedBy, Ambiguous: providers.Count > 1);
    }

    private static string ManifestDirectory(GraphNode node)
    {
        // An Xcode target's sources sit beside its .xcodeproj, not inside it.
        if (Property(node, SwiftPackageManifestExtractor.ProjectDirectoryProperty) is { } projectDirectory)
            return projectDirectory;

        var manifest = Property(node, "manifest_path") ?? node.FilePath ?? "";
        var slash = manifest.LastIndexOf('/');
        return slash < 0 ? "" : manifest[..slash];
    }

    /// <summary>Depth of <paramref name="directory"/> when it contains <paramref name="file"/>, else -1.</summary>
    private static int AncestorDepth(string directory, string file)
    {
        if (directory.Length == 0)
            return 0;
        return file.StartsWith(directory + "/", StringComparison.Ordinal)
            ? directory.Count(ch => ch == '/') + 1
            : -1;
    }

    private static IEnumerable<string> Names(GraphNode node) =>
        new[] { Property(node, "identity"), Property(node, "package_name") }
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? Property(GraphNode node, string key) =>
        node.Properties.GetValueOrDefault(key) switch
        {
            null => null,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json => json.GetString(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => "true",
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => "false",
            System.Text.Json.JsonElement json => json.ToString(),
            bool value => value ? "true" : "false",
            var value => value.ToString()
        };
}

internal sealed record SwiftModuleResolution(
    IReadOnlyList<GraphNode> Targets,
    string MatchedBy,
    bool Ambiguous);
