namespace TC.CodeGraphApi.Services;

/// <summary>
/// Production implementation backed by System.IO.
/// </summary>
public class LocalFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateFiles(string rootPath, string pattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(rootPath, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
            IgnoreInaccessible = true
        });
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
        => File.ReadAllTextAsync(path, ct);

    public string[] ReadAllLines(string path) => File.ReadAllLines(path);

    public Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct = default)
        => File.ReadAllLinesAsync(path, ct);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public string GetRelativePath(string basePath, string fullPath)
        => Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
}
