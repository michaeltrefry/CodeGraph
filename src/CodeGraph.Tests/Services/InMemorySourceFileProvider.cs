using CodeGraph.Services.Analyzers;

namespace CodeGraph.Tests.Services;

/// <summary>
/// In-memory file provider for testing analyzers without file system access.
/// </summary>
public class InMemorySourceFileProvider : ISourceFileProvider
{
    private readonly Dictionary<string, string> _files = new();

    /// <summary>
    /// Add a file with the given relative path and content.
    /// </summary>
    public void AddFile(string relativePath, string content)
    {
        // Normalize to forward slashes
        _files[relativePath.Replace('\\', '/')] = content;
    }

    public bool RootExists(string rootPath) => true;

    public IEnumerable<SourceFile> EnumerateSourceFiles(string rootPath)
    {
        return _files.Select(kvp =>
        {
            var extension = Path.GetExtension(kvp.Key);
            var lines = kvp.Value.Split('\n');
            return new SourceFile(kvp.Key, extension, lines);
        });
    }
}
