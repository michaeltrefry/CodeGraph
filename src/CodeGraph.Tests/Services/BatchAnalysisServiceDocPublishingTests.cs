using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisServiceDocPublishingTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"codegraph-doc-publish-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteCodeGraphDocsAsync_AutoCommitAndPushEnabled_CommitsAndPushesGeneratedDocs_WithoutPreconfiguredGitIdentity()
    {
        Directory.CreateDirectory(_tempRoot);

        var remotePath = Path.Combine(_tempRoot, "remote.git");
        var repoPath = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(remotePath);
        Directory.CreateDirectory(repoPath);

        await RunGitAsync(_tempRoot, $"init --bare --initial-branch=main \"{remotePath}\"");
        await RunGitAsync(repoPath, "init --initial-branch=main");
        await RunGitAsync(repoPath, $"remote add origin \"{remotePath}\"");

        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# Demo Repo\n");
        var projectDir = Path.Combine(repoPath, "src", "Demo.Project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Demo.Project.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        await RunGitAsync(repoPath, "add README.md src/Demo.Project/Demo.Project.csproj");
        await RunGitAsync(repoPath, "commit -m \"chore: seed repo\"");
        await RunGitAsync(repoPath, "push -u origin main");

        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "demo-repo",
            LocalPath = repoPath
        });

        await store.UpsertProjectAnalysisAsync("demo-repo", new StoredProjectAnalysis(
            Repo: "demo-repo",
            ProjectName: "Demo.Project",
            Summary: "Handles demo operations.",
            Confidence: ConfidenceLevel.High,
            Endpoints: [],
            Services: [],
            ExternalDependencies: [],
            DatabaseTables: [],
            ModelUsed: "test-model",
            UpdatedAt: DateTime.UtcNow));

        await store.UpsertRepositorySummaryAsync(
            "demo-repo",
            "Repository-level summary.",
            ConfidenceLevel.High,
            sourceHash: "test-batch",
            modelUsed: "test-model");

        var service = new BatchAnalysisService(
            store,
            new AnalysisProviderRegistry([], Options.Create(new AnalysisOptions { DefaultProvider = "local" })),
            new NoOpMessageBus(),
            new NoOpExclusionService(),
            Options.Create(new AnalysisOptions
            {
                AutoCommitDocs = true,
                AutoPushDocs = true,
                AutoCommitMessage = "docs(codegraph): update CODEGRAPH.md",
                AutoCommitAuthorName = "CodeGraph",
                AutoCommitAuthorEmail = "codegraph@localhost"
            }),
            new LocalFileSystem(),
            NullLogger<BatchAnalysisService>.Instance);

        await service.WriteCodeGraphDocsAsync("demo-repo", CancellationToken.None);

        File.Exists(Path.Combine(repoPath, "CODEGRAPH.md")).ShouldBeTrue();
        File.Exists(Path.Combine(projectDir, "CODEGRAPH.md")).ShouldBeTrue();

        (await RunGitCaptureAsync(repoPath, "log -1 --pretty=%s")).ShouldBe("docs(codegraph): update CODEGRAPH.md");

        var localHead = await RunGitCaptureAsync(repoPath, "rev-parse HEAD");
        var remoteHead = await RunGitCaptureAsync(_tempRoot, $"--git-dir=\"{remotePath}\" rev-parse refs/heads/main");
        remoteHead.ShouldBe(localHead);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments)
    {
        _ = await RunGitCaptureAsync(workingDirectory, arguments);
    }

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, string arguments)
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

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}: {stderr}");

        return stdout.Trim();
    }

    private sealed class NoOpMessageBus : IMessageBus
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class => Task.CompletedTask;
    }

    private sealed class NoOpExclusionService : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) => Task.FromResult<string?>(null);
        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) => Task.FromResult(new HashSet<string>());
        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() => Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);
        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) => throw new NotSupportedException();
        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();
        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) => Task.CompletedTask;
    }
}
