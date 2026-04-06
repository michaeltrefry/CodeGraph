namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Production implementation that enumerates source files via <see cref="IFileSystem"/>,
/// applying security-scan-specific filtering (excluded dirs, known extensions).
/// </summary>
public class FileSystemSourceFileProvider(IFileSystem fileSystem) : ISourceFileProvider
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", "packages", ".vs", "dist", "wwwroot"
    };

    private static readonly HashSet<string> ScanExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".json", ".xml", ".config", ".yaml", ".yml",
        ".env", ".cfg", ".ini", ".properties", ".csproj", ".sln", ".tf", ".cfm", ".cfc"
    };

    public bool RootExists(string rootPath) => fileSystem.DirectoryExists(rootPath);

    public IEnumerable<SourceFile> EnumerateSourceFiles(string rootPath)
    {
        return fileSystem.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                if (!ScanExtensions.Contains(ext)) return false;

                var relative = fileSystem.GetRelativePath(rootPath, f);
                var parts = relative.Split('/', '\\');
                return !parts.Any(p => ExcludedDirs.Contains(p));
            })
            .Select(f =>
            {
                var relativePath = fileSystem.GetRelativePath(rootPath, f);
                var extension = Path.GetExtension(f);
                var lines = fileSystem.ReadAllLines(f);
                return new SourceFile(relativePath, extension, lines);
            });
    }
}
