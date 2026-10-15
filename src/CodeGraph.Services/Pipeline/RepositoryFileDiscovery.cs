using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Indexing discovery for callers outside the pipeline that have no indexing options (the C#
/// solution analyzer, .NET support inspection): the default skip patterns, the dependency
/// directory rules and, in a git working copy, only what git lists (sc-26199).
/// </summary>
public static class RepositoryFileDiscovery
{
    private static readonly string[] DefaultSkipPatterns = new IndexingOptions().SkipPatterns;

    /// <summary>Full paths of the files under <paramref name="rootPath"/> matching <paramref name="includePatterns"/>.</summary>
    public static IReadOnlyList<string> DiscoverFiles(string rootPath, ILogger? logger, params string[] includePatterns) =>
        IndexingPipeline.DiscoverPaths(rootPath, new LocalFileSystem(), DefaultSkipPatterns, logger, includePatterns)
            .ToList();
}
