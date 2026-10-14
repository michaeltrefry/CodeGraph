using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>
    /// Directories holding SwiftPM/Xcode build output, generated workspaces or third-party
    /// checkouts. Manifests and project files under them are not the repository's own.
    /// </summary>
    private static readonly string[] SwiftGeneratedDirectories =
        [".build", ".swiftpm", "DerivedData", "SourcePackages", "Carthage", "Pods"];

    /// <summary>
    /// Runs the SwiftPM manifest extractor over every <c>Package.swift</c> and
    /// <c>Package.resolved</c> (root, <c>*.xcodeproj/project.xcworkspace</c> and
    /// <c>*.xcworkspace</c>) that the indexing skip patterns allow. Returns null for a repository
    /// with neither manifests nor an Xcode project. A manifest that cannot be read is logged and
    /// skipped.
    /// </summary>
    private ExtractionResult? ExtractSwiftPackageManifests(string rootPath, ExtractorContext context)
    {
        // Same discovery as DiscoverFiles: the matcher prunes skipped directories while walking.
        var matcher = new Matcher();
        matcher.AddIncludePatterns([
            $"**/{SwiftPackageManifestExtractor.PackageSwiftFileName}",
            $"**/{SwiftPackageManifestExtractor.PackageResolvedFileName}",
            "**/*.xcodeproj/project.pbxproj",
            "**/*.xcworkspace/contents.xcworkspacedata"
        ]);
        foreach (var pattern in _options.SkipPatterns)
            matcher.AddExclude(pattern);
        foreach (var directory in SwiftGeneratedDirectories)
            matcher.AddExclude($"**/{directory}/**");

        var manifestPaths = new List<string>();
        var hasXcodeProject = false;
        foreach (var path in matcher.GetResultsInFullPath(rootPath))
        {
            var fileName = Path.GetFileName(path);
            if (fileName is SwiftPackageManifestExtractor.PackageSwiftFileName
                or SwiftPackageManifestExtractor.PackageResolvedFileName)
                manifestPaths.Add(path);
            else if (fileName is "project.pbxproj" or "contents.xcworkspacedata")
                hasXcodeProject = true;
        }

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
