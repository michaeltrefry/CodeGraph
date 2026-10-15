using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// The arguments of the listing. <c>safe.directory=*</c> lets a root-run indexer list a bind
    /// mount owned by another user (git otherwise stops with "detected dubious ownership"); git only
    /// honours it from protected (command-line, global, system) config. Trusting the repository's
    /// config is safe for this command: <c>core.fsmonitor</c>, the one setting through which
    /// <c>ls-files</c> could run a program, is overridden on the command line (which takes
    /// precedence over repository config); <c>ls-files</c> runs no hooks, filters or external diff
    /// drivers and never writes the index.
    /// </summary>
    internal static readonly IReadOnlyList<string> ListArguments =
    [
        "-c", "safe.directory=*",
        "-c", "core.fsmonitor=false",
        "ls-files", "-z", "--stage", "--cached", "--others", "--exclude-standard"
    ];

    /// <summary>
    /// The listing for <paramref name="rootPath"/>, or null when discovery must walk the disk as it
    /// always did. Inside a <see cref="GitListingScope"/> (one indexing run) a root is listed once.
    /// Every fallback except "not a git repository" is logged as a warning: git is not installed,
    /// git fails or times out, or it lists nothing (a root inside an ignored directory of an
    /// enclosing repository).
    /// </summary>
    public static GitWorkingTreeFiles? TryList(string rootPath, ILogger? logger = null, IGitProcessRunner? runner = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        return GitListingScope.Current is { } scope
            ? scope.GetOrAdd(root, () => List(root, logger ?? scope.Logger, runner ?? scope.Runner))
            : List(root, logger ?? NullLogger.Instance, runner ?? GitProcessRunner.Instance);
    }

    private static GitWorkingTreeFiles? List(string root, ILogger logger, IGitProcessRunner runner)
    {
        if (!Directory.Exists(root))
            return null;

        GitProcessResult result;
        try
        {
            result = runner.Run(root, ListArguments, ListTimeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            logger.LogWarning(ex,
                "Could not list the git working copy at {Root}; discovery walks the disk and does not apply .gitignore or skip submodules",
                root);
            return null;
        }

        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("{Root} is not inside a git work tree; discovery walks the disk", root);
                return null;
            }

            logger.LogWarning(
                "git ls-files exited with {ExitCode} at {Root}; discovery walks the disk and does not apply .gitignore or skip submodules: {Error}",
                result.ExitCode, root, result.StandardError.Trim());
            return null;
        }

        var listing = FromListing(ParseStagedListing(result.StandardOutput));
        if (listing._files.Count > 0)
            return listing;

        logger.LogWarning(
            "git lists no files at {Root} (an ignored directory or an empty repository); discovery walks the disk",
            root);
        return null;
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
