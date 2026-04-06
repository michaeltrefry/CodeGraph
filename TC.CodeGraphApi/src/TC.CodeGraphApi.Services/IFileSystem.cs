namespace TC.CodeGraphApi.Services;

/// <summary>
/// Abstracts file system access for services, enabling unit testing without real files.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>
    /// Enumerates files matching a glob pattern (e.g. "*.csproj") under the given root.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string rootPath, string pattern, SearchOption searchOption);

    string ReadAllText(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct = default);

    string[] ReadAllLines(string path);

    Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct = default);

    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Returns the path relative to the base, normalized to forward slashes.
    /// </summary>
    string GetRelativePath(string basePath, string fullPath);
}
