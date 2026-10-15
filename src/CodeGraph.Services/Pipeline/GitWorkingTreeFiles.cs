using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// The files git itself counts as part of a working copy (sc-26199): tracked files plus untracked
/// files that are not ignored, from one <c>git ls-files -z --stage --cached --others --exclude-standard</c>
/// run in the discovery root. Ignore rules are exactly the ones git applies — every
/// <c>.gitignore</c> (nested ones included), <c>.git/info/exclude</c> and the configured
/// <c>core.excludesFile</c> (the user's global excludes) — and a tracked file stays listed even when
/// an ignore pattern matches it. A submodule (gitlink, mode 160000) and an untracked nested
/// repository are left out with everything under them. Paths are relative to the
/// discovery root, which may be a subdirectory of the work tree or reached through a symlink.
/// </summary>
internal sealed class GitWorkingTreeFiles
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _files;
    private readonly HashSet<string> _directories;

    private GitWorkingTreeFiles(HashSet<string> files, HashSet<string> directories)
    {
        _files = files;
        _directories = directories;
    }

    /// <summary>True when git lists the root-relative file <paramref name="relativePath"/>.</summary>
    public bool ContainsFile(string relativePath) => _files.Contains(Normalize(relativePath));

    /// <summary>True when the root-relative directory holds a file git lists ('' is the root).</summary>
    public bool ContainsDirectory(string relativePath) => _directories.Contains(Normalize(relativePath));

    /// <summary>True for a listed file or a directory holding one.</summary>
    public bool Contains(string relativePath) => ContainsFile(relativePath) || ContainsDirectory(relativePath);

    /// <summary>A listing given by its root-relative paths (as <c>git ls-files</c> prints them).</summary>
    internal static GitWorkingTreeFiles FromListing(IEnumerable<string> entries)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in entries.Select(Normalize).Where(entry => entry.Length > 0))
        {
            if (!files.Add(file))
                continue;
            var separator = file.LastIndexOf('/');
            while (true)
            {
                var parent = separator < 0 ? "" : file[..separator];
                if (!directories.Add(parent) || parent.Length == 0)
                    break;
                separator = parent.LastIndexOf('/');
            }
        }

        return new GitWorkingTreeFiles(files, directories);
    }

    /// <summary>
    /// The listing for <paramref name="rootPath"/>, or null when discovery must walk the disk as it
    /// always did: the root is not inside a git work tree, git is not installed, git refuses the
    /// repository (for example one owned by another user) or fails, or it lists nothing (a root
    /// inside an ignored directory of an enclosing repository).
    /// </summary>
    public static GitWorkingTreeFiles? TryList(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return null;

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        // Repository config must not run commands for a read-only listing.
        foreach (var argument in new[]
                 {
                     "-c", "core.fsmonitor=false",
                     "ls-files", "-z", "--stage", "--cached", "--others", "--exclude-standard"
                 })
            psi.ArgumentList.Add(argument);
        // Discover the repository from the root, never from an inherited hook environment.
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX" })
            psi.Environment.Remove(variable);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ListTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
                return null;

            var listing = FromListing(ParseStagedListing(stdout.GetAwaiter().GetResult()));
            _ = stderr.GetAwaiter().GetResult();
            return listing._files.Count == 0 ? null : listing;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The paths of a <c>ls-files -z --stage --cached --others</c> listing without its gitlinks and
    /// untracked nested repositories. A tracked entry reads <c>&lt;mode&gt; &lt;object&gt; &lt;stage&gt;\t&lt;path&gt;</c>
    /// (a submodule has mode 160000); an untracked one is the bare path, a nested repository's
    /// ending in '/'.
    /// </summary>
    internal static IEnumerable<string> ParseStagedListing(string listing)
    {
        foreach (var entry in listing.Split('\0'))
        {
            var match = StagedEntry.Match(entry);
            if (match.Success)
            {
                if (match.Groups["mode"].Value != GitlinkMode)
                    yield return entry[match.Length..];
            }
            else if (entry.Length > 0 && !entry.EndsWith('/'))
            {
                yield return entry;
            }
        }
    }

    private const string GitlinkMode = "160000";

    private static readonly Regex StagedEntry =
        new(@"\A(?<mode>[0-7]{6}) [0-9a-f]{40,64} [0-3]\t", RegexOptions.CultureInvariant);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
