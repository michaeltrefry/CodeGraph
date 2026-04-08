using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Services;

namespace CodeGraph.Tests.Services;

public class RepoProviderBaseTests
{
    [Fact]
    public async Task ResetToFetchedHeadAsync_DoesNotThrow_ForRepositoryWithNoCommits()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"codegraph-empty-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoPath);

        try
        {
            await RunGitAsync(repoPath, "init");

            var provider = new TestRepoProvider();

            await Should.NotThrowAsync(() =>
                provider.ResetToFetchedHeadPublicAsync(repoPath, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(repoPath))
                Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task FetchAsync_RestoresMissingOriginFetchRefspec_AndFetchesRemoteBranches()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-fetch-spec-{Guid.NewGuid():N}");
        var remotePath = Path.Combine(rootPath, "remote.git");
        var seedPath = Path.Combine(rootPath, "seed");
        var localPath = Path.Combine(rootPath, "local");

        Directory.CreateDirectory(rootPath);

        try
        {
            await RunGitAsync(rootPath, $"init --bare \"{remotePath}\"");

            Directory.CreateDirectory(seedPath);
            await RunGitAsync(seedPath, "init -b main");
            await File.WriteAllTextAsync(Path.Combine(seedPath, "README.md"), "hello");
            await RunGitAsync(seedPath, "add README.md");
            await RunGitAsync(seedPath, "config user.name \"CodeGraph Tests\"");
            await RunGitAsync(seedPath, "config user.email \"tests@example.com\"");
            await RunGitAsync(seedPath, "commit -m initial");
            await RunGitAsync(seedPath, $"remote add origin \"{remotePath}\"");
            await RunGitAsync(seedPath, "push origin main");

            Directory.CreateDirectory(localPath);
            await RunGitAsync(localPath, "init");
            await RunGitAsync(localPath, $"remote add origin \"{remotePath}\"");

            var provider = new TestRepoProvider();
            await provider.FetchPublicAsync(localPath, CancellationToken.None);

            var fetchRefspec = await RunGitOutputAsync(localPath, "config --get-all remote.origin.fetch");
            fetchRefspec.Trim().ShouldBe("+refs/heads/*:refs/remotes/origin/*");

            var remoteMain = await RunGitOutputAsync(localPath, "rev-parse --verify origin/main");
            remoteMain.Trim().Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, stderr);
    }

    private static async Task<string> RunGitOutputAsync(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, stderr);
        return stdout;
    }

    private sealed class TestRepoProvider()
        : RepoProviderBase(Path.Combine(Path.GetTempPath(), $"codegraph-cache-{Guid.NewGuid():N}"), NullLogger.Instance)
    {
        public Task ResetToFetchedHeadPublicAsync(string repoPath, CancellationToken ct) =>
            ResetToFetchedHeadAsync(repoPath, ct);

        public Task FetchPublicAsync(string repoPath, CancellationToken ct) =>
            FetchAsync(repoPath, ct);
    }
}
