using System.Text;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Current extraction version of every indexed file type, so extractor improvements reach
/// repositories whose files have not changed (self-maintaining: no manual rebuild needed).
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract — bump the version whenever extraction output changes.</b> Any change that makes
/// an extractor or analyzer emit different nodes, edges, unresolved calls/imports or properties
/// for the same file content MUST bump that component's version:
/// <see cref="ICodeExtractor.GetExtractionVersion"/> (per-file extractors, including each
/// tree-sitter <c>LanguageSpec.ExtractionVersion</c>), <see cref="ISolutionAnalyzer.ExtractionVersion"/>,
/// <see cref="ITypeScriptAnalyzer.ExtractionVersion"/> (also for Node.js sidecar changes),
/// <see cref="IRustAnalyzer.ExtractionVersion"/>, or <see cref="PipelineVersion"/> for changes to
/// the pipeline's own merge/resolution logic. Without a bump, unchanged files keep their old
/// extraction until their content changes.
/// </para>
/// <para>
/// Each indexed file stores the version it was extracted with (<c>file_hashes.extraction_version</c>);
/// incremental indexing re-extracts a file whose stored version differs (or is missing). The
/// <see cref="Fingerprint"/> over all file types is stored on <c>sync_state</c> so a routine
/// (SkipIfUpToDate) sync re-indexes a repository whose HEAD is unchanged after a bump. Only the
/// file types whose version changed are re-extracted; no LLM work is triggered by such a run.
/// </para>
/// <para>
/// NuGet reference extraction is not versioned: it runs over every project on every index.
/// </para>
/// <para>
/// Cargo and Swift package manifests are extracted on every index, but their slices are written
/// only when they changed. <c>Package.swift</c> files are source files and carry
/// <see cref="ISwiftPackageManifestExtractor.ExtractionVersion"/> in their version. The manifests
/// that are not source files (<see cref="TrackedManifestNames"/>: <c>Cargo.toml</c>,
/// <c>Package.resolved</c>, <c>project.pbxproj</c>) record their content hash with
/// <see cref="ForManifest"/> (pipeline version and <see cref="ICargoManifestExtractor.ExtractionVersion"/>
/// or <see cref="ISwiftPackageManifestExtractor.ExtractionVersion"/>) in <c>file_hashes</c>; a group
/// of them (Cargo, or Swift together with <c>Package.swift</c>) is rewritten when any of its
/// manifests is new, changed, outdated or gone. Those versions are part of the
/// <see cref="Fingerprint"/>, so a bump triggers the refresh run that rewrites the slices.
/// </para>
/// <para>
/// The discovery exclusions (skip patterns, dependency directory rules and the git working-copy
/// rule, <see cref="IndexingPipeline.GitWorkingCopyRule"/>) are part of the
/// <see cref="Fingerprint"/> but not of any file's version: changing them re-runs a routine sync at
/// an unchanged commit, whose full scan removes the files that left the indexed universe and
/// extracts the files that entered it, without re-extracting the files that stayed.
/// </para>
/// <para>
/// The effective file size limits are part of the <see cref="Fingerprint"/> too. A file skipped as
/// oversize records a version naming its limit, and the pipeline expects that version for every
/// file over the current limit, so the refresh run after a limit change extracts a file that is no
/// longer oversize and empties the slice of a file that now is.
/// </para>
/// </remarks>
public sealed class ExtractionVersionCatalog
{
    /// <summary>
    /// Version of the pipeline's own merge/resolution logic; applies to every file.
    /// 2: name-based call resolution is scoped by language family (<see cref="CallLanguages"/>).
    /// 3: calls bind only to callables, or to types of the caller's own family; a caller's family
    /// falls back to its qualified name's file (<see cref="CallLanguages.IsCallTarget"/>, sc-26145).
    /// 4: a call crosses a language-family boundary only as a bare call to a free function (never a
    /// method, never a receiver call; Rust → C++ only with C linkage), and a Swift call binds only
    /// within the caller's own module and the modules its file imports (sc-26145).
    /// </summary>
    /// 5 (sc-27973): JS/JSX repository analysis, semantic outgoing-slice reconciliation,
    /// complete-output preservation and component callable ownership impact traversal.
    /// 6 (sc-27972): native endpoint/Tauri reconciliation and local/cross-repository HTTP binding.
    /// 7 (sc-27972): scoped callable-edge admission and case-insensitive analyzer version coverage.
    /// 8: bounded native registration reads and language-specific analyzer case selection.
    /// 9: admitted bounded Go manifests and contained regular-file registration input.
    /// 10: indexed/cancellable Tauri analysis with deterministic work and depth limits.
    public const int PipelineVersion = 11; // Shared bounded/indexed native endpoint analysis.

    private static readonly string[] SolutionAnalyzerExtensions = [".cs"];
    private static readonly string[] TypeScriptAnalyzerExtensions = [".js", ".jsx", ".ts", ".tsx"];
    private static readonly string[] RustAnalyzerExtensions = [".rs"];

    private readonly IReadOnlyList<ICodeExtractor> _extractors;
    private readonly ExtractorExtensions _extensions;
    private readonly ISolutionAnalyzer? _solutionAnalyzer;
    private readonly ITypeScriptAnalyzer? _typeScriptAnalyzer;
    private readonly IRustAnalyzer? _rustAnalyzer;
    private readonly ISwiftPackageManifestExtractor? _swiftPackageManifestExtractor;
    private readonly ICargoManifestExtractor? _cargoManifestExtractor;
    private readonly IReadOnlyList<string>? _discoveryExcludePatterns;
    private readonly string? _fileSizeLimits;

    public ExtractionVersionCatalog(
        IEnumerable<ICodeExtractor> extractors,
        ISolutionAnalyzer? solutionAnalyzer = null,
        ITypeScriptAnalyzer? typeScriptAnalyzer = null,
        IRustAnalyzer? rustAnalyzer = null,
        ISwiftPackageManifestExtractor? swiftPackageManifestExtractor = null,
        IEnumerable<string>? discoveryExcludePatterns = null,
        string? fileSizeLimits = null,
        ICargoManifestExtractor? cargoManifestExtractor = null)
    {
        _cargoManifestExtractor = cargoManifestExtractor;
        _extractors = extractors.ToList();
        _extensions = new ExtractorExtensions(_extractors);
        _solutionAnalyzer = solutionAnalyzer;
        _typeScriptAnalyzer = typeScriptAnalyzer;
        _rustAnalyzer = rustAnalyzer;
        _swiftPackageManifestExtractor = swiftPackageManifestExtractor;
        _discoveryExcludePatterns = discoveryExcludePatterns?.ToList();
        _fileSizeLimits = fileSizeLimits;
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>
    /// Stable digest of the extraction versions of every supported file type. Changes whenever
    /// any component version changes.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// The extraction version for a file, derived from its extension (and, for a SwiftPM
    /// <c>Package.swift</c>, the manifest extractor version).
    /// </summary>
    public string ForPath(string path)
    {
        var version = ForExtension(Path.GetExtension(path));
        return IsSwiftPackageManifest(path)
            ? $"{version};{_swiftPackageManifestExtractor!.GetType().Name}={_swiftPackageManifestExtractor.ExtractionVersion}"
            : version;
    }

    /// <summary>
    /// Manifests whose package slice is not a source file's: they are tracked by content hash and
    /// version (<see cref="ForManifest"/>) so an unchanged one is not rewritten on every run.
    /// </summary>
    public static readonly IReadOnlySet<string> TrackedManifestNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Cargo.toml",
        SwiftPackageManifestExtractor.PackageResolvedFileName,
        XcodeProjectReader.ProjectFileName
    };

    /// <summary>
    /// The version recorded with a tracked manifest's content hash (<see cref="TrackedManifestNames"/>):
    /// the pipeline version and the version of the extractor that reads it. Null for any other file,
    /// or when that extractor is not registered.
    /// </summary>
    public string? ForManifest(string path) => Path.GetFileName(path) switch
    {
        "Cargo.toml" when _cargoManifestExtractor is not null =>
            $"p{PipelineVersion};{_cargoManifestExtractor.GetType().Name}={_cargoManifestExtractor.ExtractionVersion}",
        SwiftPackageManifestExtractor.PackageResolvedFileName or XcodeProjectReader.ProjectFileName
            when _swiftPackageManifestExtractor is not null =>
            $"p{PipelineVersion};{_swiftPackageManifestExtractor.GetType().Name}={_swiftPackageManifestExtractor.ExtractionVersion}",
        _ => null
    };

    private bool IsSwiftPackageManifest(string path) =>
        _swiftPackageManifestExtractor is not null &&
        string.Equals(Path.GetFileName(path), SwiftPackageManifestExtractor.PackageSwiftFileName,
            StringComparison.Ordinal);

    public string ForExtension(string extension)
    {
        var version = new StringBuilder($"p{PipelineVersion}");

        // Same selection as per-file extraction in IndexingPipeline: the first registered
        // extractor that supports the extension, matched by ordinal comparison.
        var extractor = _extensions.ExtractorFor(extension);
        if (extractor is not null)
            Append(version, extractor.GetType().Name, extractor.GetExtractionVersion(extension));

        if (_solutionAnalyzer is not null && Covers(SolutionAnalyzerExtensions, extension))
            Append(version, _solutionAnalyzer.GetType().Name, _solutionAnalyzer.ExtractionVersion);
        if (_typeScriptAnalyzer is not null && TypeScriptAnalyzerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            Append(version, _typeScriptAnalyzer.GetType().Name, _typeScriptAnalyzer.ExtractionVersion);
        if (_rustAnalyzer is not null && Covers(RustAnalyzerExtensions, extension))
            Append(version, _rustAnalyzer.GetType().Name, _rustAnalyzer.ExtractionVersion);

        return version.ToString();
    }

    private string ComputeFingerprint()
    {
        var lines = _extractors
            .SelectMany(e => e.SupportedExtensions)
            .Concat(_solutionAnalyzer is null ? [] : SolutionAnalyzerExtensions)
            .Concat(_typeScriptAnalyzer is null ? [] : TypeScriptAnalyzerExtensions)
            .Concat(_rustAnalyzer is null ? [] : RustAnalyzerExtensions)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(extension => $"{extension}={ForExtension(extension)}")
            .Concat(_swiftPackageManifestExtractor is null
                ? []
                : [$"{SwiftPackageManifestExtractor.PackageSwiftFileName}={ForPath(SwiftPackageManifestExtractor.PackageSwiftFileName)}"])
            .Concat(TrackedManifestNames.Order(StringComparer.Ordinal)
                .Select(name => (Name: name, Version: ForManifest(name)))
                .Where(manifest => manifest.Version is not null)
                .Select(manifest => $"manifest:{manifest.Name}={manifest.Version}"))
            .Concat(_discoveryExcludePatterns is null
                ? []
                : [$"discovery-excludes={string.Join('|', _discoveryExcludePatterns)}"])
            .Concat(_fileSizeLimits is null ? [] : [$"max-file-size-kb={_fileSizeLimits}"]);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(bytes));
    }

    private static bool Covers(string[] extensions, string extension) =>
        extensions.Contains(extension, StringComparer.Ordinal);

    private static void Append(StringBuilder version, string component, string componentVersion) =>
        version.Append(';').Append(component).Append('=').Append(componentVersion);
}
