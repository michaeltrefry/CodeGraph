using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// A directory tree for glob discovery that hides every subdirectory <paramref name="exclude"/>
/// rejects (by full path), so the matcher never walks into it.
/// </summary>
internal sealed class DiscoveryDirectory(DirectoryInfoBase inner, Func<string, bool> exclude) : DirectoryInfoBase
{
    public override string Name => inner.Name;

    public override string FullName => inner.FullName;

    public override DirectoryInfoBase? ParentDirectory => inner.ParentDirectory;

    public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
    {
        foreach (var entry in inner.EnumerateFileSystemInfos())
        {
            if (entry is not DirectoryInfoBase directory)
                yield return entry;
            else if (!exclude(directory.FullName))
                yield return new DiscoveryDirectory(directory, exclude);
        }
    }

    public override DirectoryInfoBase? GetDirectory(string path) =>
        inner.GetDirectory(path) is { } directory && !exclude(directory.FullName)
            ? new DiscoveryDirectory(directory, exclude)
            : null;

    public override FileInfoBase? GetFile(string path) => inner.GetFile(path);
}
