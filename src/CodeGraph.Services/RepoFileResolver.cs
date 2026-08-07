using System.Runtime.InteropServices;
using System.Text;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Win32.SafeHandles;

namespace CodeGraph.Services;

/// <summary>
/// Resolves and opens files contained by an indexed repository checkout.
/// </summary>
public static class RepoFileResolver
{
    private static readonly char[] PortableSeparators = ['/', '\\'];
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Resolves the canonical path for a file in a repository. Cache is checked first,
    /// then the indexed local path. Unsafe portable path syntax and physical escapes are rejected.
    /// </summary>
    public static string? Resolve(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath)
        => ResolveCandidate(repoName, relativeFilePath, cachePath, localPath)?.Path;

    /// <summary>
    /// Resolves a file using repository metadata from the graph store. A repository must
    /// already be indexed; a cache directory named only by caller input is never trusted.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string repoName,
        string relativeFilePath,
        RepositorySourceOptions sourceOptions,
        IGraphStore store)
    {
        var project = await store.GetRepositoryByName(repoName);
        if (project is null)
            return null;

        return Resolve(project.Name, relativeFilePath, sourceOptions.ReposCachePath, project.LocalPath);
    }

    /// <summary>
    /// Opens a repository file for reading and validates the opened handle's physical path
    /// on Windows and Linux. Other Unix platforms revalidate the physical path after open.
    /// </summary>
    public static async Task<FileStream?> OpenReadAsync(
        string repoName,
        string relativeFilePath,
        RepositorySourceOptions sourceOptions,
        IGraphStore store,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var project = await store.GetRepositoryByName(repoName);
        if (project is null)
            return null;

        return OpenRead(project.Name, relativeFilePath, sourceOptions.ReposCachePath, project.LocalPath);
    }

    /// <summary>
    /// Opens a repository file when the caller already obtained the indexed repository root.
    /// </summary>
    public static FileStream? OpenRead(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath)
        => OpenReadCore(repoName, relativeFilePath, cachePath, localPath, beforeOpen: null);

    internal static FileStream? OpenReadForTesting(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath,
        Action beforeOpen)
        => OpenReadCore(repoName, relativeFilePath, cachePath, localPath, beforeOpen);

    private static FileStream? OpenReadCore(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath,
        Action? beforeOpen)
    {
        var resolved = ResolveCandidate(repoName, relativeFilePath, cachePath, localPath);
        if (resolved is null)
            return null;

        FileStream? stream = null;
        try
        {
            beforeOpen?.Invoke();
            stream = new FileStream(
                resolved.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var openedPath = GetOpenedHandlePath(stream.SafeFileHandle, resolved.Path);
            if (openedPath is null || !IsContainedBy(openedPath, resolved.Root))
            {
                stream.Dispose();
                return null;
            }

            return stream;
        }
        catch (IOException)
        {
            stream?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    public static async Task<string[]?> ReadAllLinesAsync(
        string repoName,
        string relativeFilePath,
        RepositorySourceOptions sourceOptions,
        IGraphStore store,
        CancellationToken ct = default)
    {
        await using var stream = await OpenReadAsync(repoName, relativeFilePath, sourceOptions, store, ct);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    public static async Task<string?> ReadAllTextAsync(
        string repoName,
        string relativeFilePath,
        RepositorySourceOptions sourceOptions,
        IGraphStore store,
        CancellationToken ct = default)
    {
        await using var stream = await OpenReadAsync(repoName, relativeFilePath, sourceOptions, store, ct);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task<string?> ReadAllTextAsync(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath,
        CancellationToken ct = default)
    {
        await using var stream = OpenRead(repoName, relativeFilePath, cachePath, localPath);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            return false;

        // Apply both Unix and Windows rules on every host. Path.IsPathRooted alone is
        // host-dependent and would accept drive, UNC, and device paths on Unix.
        if (path[0] is '/' or '\\' ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            return false;
        }

        if (path.Contains('/') && path.Contains('\\'))
            return false;

        var segments = path.Split(PortableSeparators, StringSplitOptions.None);
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Contains(':') ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.')))
        {
            return false;
        }

        foreach (var segment in segments)
        {
            var basename = segment.Split('.')[0];
            if (WindowsDeviceNames.Contains(basename))
                return false;
        }

        return true;
    }

    private static ResolvedFile? ResolveCandidate(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath)
    {
        if (!IsSafeRelativePath(relativeFilePath))
            return null;

        var normalized = string.Join(Path.DirectorySeparatorChar, relativeFilePath.Split(PortableSeparators));

        if (!string.IsNullOrWhiteSpace(cachePath) && IsSafeRelativePath(repoName))
        {
            var cacheRoot = TryGetContainedRepositoryRoot(cachePath, repoName);
            var cached = cacheRoot is null ? null : TryResolveUnderRoot(cacheRoot, normalized);
            if (cached is not null)
                return cached;
        }

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var localRoot = TryGetPhysicalDirectory(localPath);
            var local = localRoot is null ? null : TryResolveUnderRoot(localRoot, normalized);
            if (local is not null)
                return local;
        }

        return null;
    }

    private static string? TryGetContainedRepositoryRoot(string cachePath, string repoName)
    {
        var physicalCacheRoot = TryGetPhysicalDirectory(cachePath);
        if (physicalCacheRoot is null)
            return null;

        var repositoryRoot = TryGetPhysicalDirectory(Path.Combine(physicalCacheRoot, repoName));
        return repositoryRoot is not null && IsContainedBy(repositoryRoot, physicalCacheRoot)
            ? repositoryRoot
            : null;
    }

    private static ResolvedFile? TryResolveUnderRoot(string physicalRoot, string relativePath)
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(physicalRoot, relativePath));
            if (!File.Exists(candidate))
                return null;

            var physicalCandidate = TryGetPhysicalFile(candidate);
            return physicalCandidate is not null && IsContainedBy(physicalCandidate, physicalRoot)
                ? new ResolvedFile(physicalCandidate, physicalRoot)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryGetPhysicalDirectory(string path)
        => Directory.Exists(path) ? TryGetPhysicalPath(new DirectoryInfo(Path.GetFullPath(path))) : null;

    private static string? TryGetPhysicalFile(string path)
        => File.Exists(path) ? TryGetPhysicalPath(new FileInfo(Path.GetFullPath(path))) : null;

    private static string? TryGetPhysicalPath(FileSystemInfo entry)
    {
        try
        {
            var fullPath = entry.FullName;
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return null;

            var current = root;
            var remainder = fullPath[root.Length..];
            var segments = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                FileSystemInfo component = i == segments.Length - 1 && entry is FileInfo
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);

                component.Refresh();
                if ((component.Attributes & FileAttributes.ReparsePoint) == 0)
                    continue;

                var target = component.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !target.Exists)
                    return null;

                current = TryGetPhysicalPath(target) ?? target.FullName;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsContainedBy(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (canonicalCandidate.Equals(canonicalRoot, comparison))
            return false;

        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(prefix, comparison);
    }

    private static string? GetOpenedHandlePath(SafeFileHandle handle, string fallbackPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetWindowsHandlePath(handle);
            if (OperatingSystem.IsLinux())
                return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}")
                    .ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            // Other Unix platforms still receive path and symlink containment, with a
            // second physical-path check after the stream is open. Windows and Linux,
            // including the production containers, validate the opened handle itself.
            return TryGetPhysicalFile(fallbackPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetWindowsHandlePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return null;
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                return null;
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private sealed record ResolvedFile(string Path, string Root);
}
