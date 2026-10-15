namespace CodeGraph.Services.Pipeline;

/// <summary>
/// The view of one repository tree that discovery's dependency-directory rules probe: whether a
/// repo-relative file exists and the names of a directory's direct children (files and
/// directories). Backed by the checkout on disk for indexing, or by a commit's file list for
/// branch overlays, so both decide identically. Rules only probe next to or directly inside the
/// directory they judge, keeping disk listings to one level.
/// </summary>
public sealed class RepositoryTree
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyCollection<string>> _children;

    private RepositoryTree(Func<string, bool> fileExists, Func<string, IReadOnlyCollection<string>> children)
    {
        _fileExists = fileExists;
        _children = children;
    }

    /// <summary>True when the repo-relative file <paramref name="relativePath"/> exists.</summary>
    public bool FileExists(string relativePath) => _fileExists(Normalize(relativePath));

    /// <summary>Names of the direct children of the repo-relative directory ('' is the root).</summary>
    public IReadOnlyCollection<string> ListDirectory(string relativeDirectory) => _children(Normalize(relativeDirectory));

    /// <summary>A tree given by its repo-relative file paths (for example <c>git ls-tree -r</c>).</summary>
    public static RepositoryTree FromFiles(IEnumerable<string> relativeFiles)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in relativeFiles.Select(Normalize).Where(file => file.Length > 0))
        {
            if (!files.Add(file))
                continue;
            var path = file;
            while (path.Length > 0)
            {
                var separator = path.LastIndexOf('/');
                var parent = separator < 0 ? "" : path[..separator];
                var name = separator < 0 ? path : path[(separator + 1)..];
                if (!children.TryGetValue(parent, out var names))
                    children[parent] = names = new HashSet<string>(StringComparer.Ordinal);
                if (!names.Add(name))
                    break;
                path = parent;
            }
        }

        return new RepositoryTree(
            files.Contains,
            directory => children.TryGetValue(directory, out var names) ? names : []);
    }

    /// <summary>The checkout at <paramref name="rootPath"/>.</summary>
    public static RepositoryTree FromFileSystem(string rootPath, IFileSystem fileSystem)
    {
        string Full(string relative) => relative.Length == 0 ? rootPath : Path.Combine(rootPath, relative);
        return new RepositoryTree(
            relative => fileSystem.FileExists(Full(relative)),
            relative =>
            {
                var directory = Full(relative);
                if (!fileSystem.DirectoryExists(directory))
                    return [];
                return fileSystem.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Concat(fileSystem.EnumerateDirectories(directory))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToList();
            });
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
