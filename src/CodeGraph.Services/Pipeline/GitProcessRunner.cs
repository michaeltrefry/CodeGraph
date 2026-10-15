using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

/// <summary>Exit code and output of one git command.</summary>
internal sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs a git command (a seam for discovery tests).</summary>
internal interface IGitProcessRunner
{
    /// <summary>
    /// Runs git with <paramref name="arguments"/> in <paramref name="workingDirectory"/>. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> when git is not installed and
    /// <see cref="TimeoutException"/> when it does not finish within <paramref name="timeout"/>.
    /// </summary>
    GitProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>The git CLI, with the repository discovered from the working directory.</summary>
internal sealed class GitProcessRunner(IReadOnlyDictionary<string, string>? environment = null) : IGitProcessRunner
{
    public static readonly GitProcessRunner Instance = new();

    public GitProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        // Discover the repository from the working directory, never from an inherited hook
        // environment, and keep git's messages untranslated so failures can be classified.
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX" })
            psi.Environment.Remove(variable);
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANGUAGE"] = "C";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', arguments)} did not finish within {timeout}.");
        }

        process.WaitForExit();
        return new GitProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

/// <summary>
/// One indexing run's git listings: discovery runs many times per run (source files, Cargo and
/// Swift manifests, the TypeScript scope and clamp), and each root is listed once. Ambient through
/// the run's async flow; a nested <see cref="Begin"/> joins the active scope.
/// </summary>
internal sealed class GitListingScope : IDisposable
{
    private static readonly AsyncLocal<GitListingScope?> Ambient = new();

    private readonly ConcurrentDictionary<string, Lazy<GitWorkingTreeFiles?>> _listings = new(StringComparer.Ordinal);

    private GitListingScope(IGitProcessRunner runner, ILogger logger)
    {
        Runner = runner;
        Logger = logger;
    }

    public static GitListingScope? Current => Ambient.Value;

    public IGitProcessRunner Runner { get; }

    public ILogger Logger { get; }

    public static IDisposable Begin(IGitProcessRunner runner, ILogger logger)
    {
        if (Ambient.Value is { } active)
            return new GitListingScope(active.Runner, active.Logger); // never ambient: disposing it is a no-op
        var scope = new GitListingScope(runner, logger);
        Ambient.Value = scope;
        return scope;
    }

    public GitWorkingTreeFiles? GetOrAdd(string root, Func<GitWorkingTreeFiles?> list) =>
        _listings.GetOrAdd(root, _ => new Lazy<GitWorkingTreeFiles?>(list)).Value;

    public void Dispose()
    {
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = null;
    }
}
