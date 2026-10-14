using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>
    /// Runs the SwiftPM manifest extractor over every <c>Package.swift</c>,
    /// <c>Package.resolved</c> (root, <c>*.xcodeproj/project.xcworkspace</c> and
    /// <c>*.xcworkspace</c>) and <c>*.xcodeproj/project.pbxproj</c> (native targets as Swift
    /// modules) that discovery includes, and reports every manifest it found in
    /// <paramref name="manifestRelativePaths"/>. Returns null for a repository with neither
    /// manifests nor an Xcode project. A manifest that cannot be read is logged and skipped.
    /// </summary>
    private ExtractionResult? ExtractSwiftPackageManifests(
        string rootPath, ExtractorContext context, out IReadOnlyList<string> manifestRelativePaths)
    {
        // Same exclusions as DiscoverFiles: the walk prunes excluded directories.
        var discovered = DiscoverPaths(
            rootPath,
            $"**/{SwiftPackageManifestExtractor.PackageSwiftFileName}",
            $"**/{SwiftPackageManifestExtractor.PackageResolvedFileName}",
            "**/*.xcodeproj/project.pbxproj",
            "**/*.xcworkspace/contents.xcworkspacedata");

        var manifestPaths = new List<string>();
        var hasXcodeProject = false;
        foreach (var path in discovered)
        {
            var fileName = Path.GetFileName(path);
            if (fileName is SwiftPackageManifestExtractor.PackageSwiftFileName
                or SwiftPackageManifestExtractor.PackageResolvedFileName
                or XcodeProjectReader.ProjectFileName)
                manifestPaths.Add(path);
            if (fileName is XcodeProjectReader.ProjectFileName or "contents.xcworkspacedata")
                hasXcodeProject = true;
        }

        manifestRelativePaths = manifestPaths
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .ToList();
        if (manifestPaths.Count == 0 && !hasXcodeProject)
            return null;

        var manifests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in manifestPaths)
        {
            try
            {
                manifests[path] = _fileSystem.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping unreadable Swift package manifest {Manifest} in {Project}",
                    NormalizeRelativePath(Path.GetRelativePath(rootPath, path)), context.ProjectName);
            }
        }

        return _swiftPackageManifestExtractor!.Extract(manifests, hasXcodeProject, context);
    }
}
