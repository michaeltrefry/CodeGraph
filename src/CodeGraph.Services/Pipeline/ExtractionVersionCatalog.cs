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
/// Cargo manifest and NuGet reference extraction are not versioned: they run over every manifest
/// on every index.
/// </para>
/// <para>
/// Swift package manifests also run on every index, but their output reaches unchanged repositories
/// only through a versioned file: <c>Package.swift</c> files carry
/// <see cref="ISwiftPackageManifestExtractor.ExtractionVersion"/> (so a bump re-extracts them and
/// replaces their declared-dependency slice) and the version is part of the <see cref="Fingerprint"/>
/// (so a bump triggers the refresh run that rewrites the <c>Package.resolved</c> pins).
/// </para>
/// <para>
/// The discovery exclusions (skip patterns and generated dependency directories) are part of the
/// <see cref="Fingerprint"/> but not of any file's version: changing them re-runs a routine sync at
/// an unchanged commit, whose full scan removes the files that left the indexed universe without
/// re-extracting the files that stayed.
/// </para>
/// </remarks>
public sealed class ExtractionVersionCatalog
{
    /// <summary>
    /// Version of the pipeline's own merge/resolution logic; applies to every file.
    /// 2: name-based call resolution is scoped by language family (<see cref="CallLanguages"/>).
    /// </summary>
    public const int PipelineVersion = 2;

    private static readonly string[] SolutionAnalyzerExtensions = [".cs"];
    private static readonly string[] TypeScriptAnalyzerExtensions = [".ts", ".tsx"];
    private static readonly string[] RustAnalyzerExtensions = [".rs"];

    private readonly IReadOnlyList<ICodeExtractor> _extractors;
    private readonly ISolutionAnalyzer? _solutionAnalyzer;
    private readonly ITypeScriptAnalyzer? _typeScriptAnalyzer;
    private readonly IRustAnalyzer? _rustAnalyzer;
    private readonly ISwiftPackageManifestExtractor? _swiftPackageManifestExtractor;
    private readonly IReadOnlyList<string>? _discoveryExcludePatterns;

    public ExtractionVersionCatalog(
        IEnumerable<ICodeExtractor> extractors,
        ISolutionAnalyzer? solutionAnalyzer = null,
        ITypeScriptAnalyzer? typeScriptAnalyzer = null,
        IRustAnalyzer? rustAnalyzer = null,
        ISwiftPackageManifestExtractor? swiftPackageManifestExtractor = null,
        IEnumerable<string>? discoveryExcludePatterns = null)
    {
        _extractors = extractors.ToList();
        _solutionAnalyzer = solutionAnalyzer;
        _typeScriptAnalyzer = typeScriptAnalyzer;
        _rustAnalyzer = rustAnalyzer;
        _swiftPackageManifestExtractor = swiftPackageManifestExtractor;
        _discoveryExcludePatterns = discoveryExcludePatterns?.ToList();
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

    private bool IsSwiftPackageManifest(string path) =>
        _swiftPackageManifestExtractor is not null &&
        string.Equals(Path.GetFileName(path), SwiftPackageManifestExtractor.PackageSwiftFileName,
            StringComparison.Ordinal);

    public string ForExtension(string extension)
    {
        var version = new StringBuilder($"p{PipelineVersion}");

        // Same selection as per-file extraction in IndexingPipeline: the first registered
        // extractor that supports the extension.
        var extractor = _extractors.FirstOrDefault(e => e.SupportedExtensions.Contains(extension));
        if (extractor is not null)
            Append(version, extractor.GetType().Name, extractor.GetExtractionVersion(extension));

        if (_solutionAnalyzer is not null && Covers(SolutionAnalyzerExtensions, extension))
            Append(version, _solutionAnalyzer.GetType().Name, _solutionAnalyzer.ExtractionVersion);
        if (_typeScriptAnalyzer is not null && Covers(TypeScriptAnalyzerExtensions, extension))
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
            .Concat(_discoveryExcludePatterns is null
                ? []
                : [$"discovery-excludes={string.Join('|', _discoveryExcludePatterns)}"]);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(bytes));
    }

    private static bool Covers(string[] extensions, string extension) =>
        extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static void Append(StringBuilder version, string component, string componentVersion) =>
        version.Append(';').Append(component).Append('=').Append(componentVersion);
}
