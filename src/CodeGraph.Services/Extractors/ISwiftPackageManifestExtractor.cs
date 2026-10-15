using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Extracts Swift Package Manager package definitions, declared dependencies
/// (<c>Package.swift</c>) and resolved pins (<c>Package.resolved</c> v1/v2/v3) from a repository,
/// and classifies it as Swift/Xcode or Swift/SwiftPM.
/// </summary>
public interface ISwiftPackageManifestExtractor
{
    /// <summary>
    /// Version of the extraction output. Bump whenever the emitted nodes, edges or properties
    /// change for the same manifest content (see <c>ExtractionVersionCatalog</c>).
    /// </summary>
    string ExtractionVersion { get; }

    /// <param name="manifests">Full path to content of every <c>Package.swift</c>,
    /// <c>Package.resolved</c> and <c>*.xcodeproj/project.pbxproj</c> in the repository.</param>
    /// <param name="hasXcodeProject">Whether the repository contains an <c>.xcodeproj</c> or
    /// <c>.xcworkspace</c>.</param>
    /// <param name="context">Extraction context; <see cref="ExtractorContext.RootPath"/> anchors
    /// relative manifest paths.</param>
    /// <returns>Package nodes and edges. <see cref="ExtractionResult.ProcessedFiles"/> lists the
    /// relative paths of the manifests that parsed; a manifest that fails to parse is logged and
    /// skipped.</returns>
    ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        bool hasXcodeProject,
        ExtractorContext context);
}
