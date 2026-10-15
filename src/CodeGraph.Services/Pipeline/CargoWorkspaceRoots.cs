using System.Text.RegularExpressions;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// The Cargo manifests Rust project (SCIP) analysis starts from. A repository can hold several
/// independent Cargo workspaces (for example <c>adapters/catalog</c> and <c>engines/catalog</c>
/// with no root manifest); each one must be analyzed, not just the first manifest found.
/// </summary>
internal static partial class CargoWorkspaceRoots
{
    /// <summary>
    /// Every workspace root manifest plus every package manifest that no workspace directory
    /// encloses, in ordinal path order. A manifest below a directory whose <c>Cargo.toml</c> declares
    /// <c>[workspace]</c> belongs to that workspace (cargo resolves a member to its workspace root),
    /// so it is analyzed through the root rather than again on its own. <paramref name="discoveredManifests"/>
    /// are the <c>Cargo.toml</c> files discovery includes (discovery exclusions and <c>target/</c>
    /// build output already removed), so a copy under an agent worktree or dependency directory is
    /// never analyzed.
    /// </summary>
    public static IReadOnlyList<string> Find(IFileSystem fileSystem, IEnumerable<string> discoveredManifests)
    {
        var manifests = discoveredManifests
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var workspaceDirectories = manifests
            .Where(manifest => DeclaresWorkspace(fileSystem, manifest))
            .Select(DirectoryOf)
            .ToList();

        return manifests
            .Where(manifest => !workspaceDirectories.Any(workspace => IsStrictlyBelow(DirectoryOf(manifest), workspace)))
            .ToList();
    }

    private static bool DeclaresWorkspace(IFileSystem fileSystem, string manifest)
    {
        try
        {
            return WorkspaceTable().IsMatch(fileSystem.ReadAllText(manifest));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string DirectoryOf(string manifest) => Path.GetDirectoryName(manifest) ?? "";

    private static bool IsStrictlyBelow(string directory, string ancestor) =>
        directory.Length > ancestor.Length &&
        directory.StartsWith(ancestor, StringComparison.Ordinal) &&
        directory[ancestor.Length] == Path.DirectorySeparatorChar;

    /// <summary><c>[workspace]</c> or a <c>[workspace.*]</c> sub-table header, which also defines it.</summary>
    [GeneratedRegex(@"^\s*\[\s*workspace\s*(\]|\.)", RegexOptions.Multiline)]
    private static partial Regex WorkspaceTable();
}
