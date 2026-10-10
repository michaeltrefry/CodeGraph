using System.Text;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class BranchOverlayServiceTests : IDisposable
{
    private const string Project = "OverlayRepo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-overlay-test-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _graph = new();
    private readonly InMemoryBranchOverlayStore _overlays = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task IndexOverlayAsync_FetchedBranch_HoldsOnlyChangedFilesAndLeavesDefaultGraphUnchanged()
    {
        var (remote, cache, baseSha) = CreateRemoteAndSingleBranchCache();

        // The branch is created on the remote after the provider's single-branch clone, so the
        // overlay must fetch it explicitly (factory/* branches are never in the default clone).
        GitFixture.Run(remote, "checkout", "-q", "-b", "factory/feature");
        GitFixture.Write(remote, "src/a.py", "def alpha\ncall beta\ndef alpha_two\n");
        GitFixture.Write(remote, "src/e.py", "def epsilon\ncall beta\n");
        GitFixture.Run(remote, "rm", "-q", "src/c.py");
        GitFixture.Run(remote, "mv", "src/d.py", "src/f.py");
        GitFixture.Write(remote, "README.md", "changed docs\n");
        GitFixture.Write(remote, "node_modules/pkg/vendored.py", "def vendored\n");
        GitFixture.Run(remote, "add", "-A");
        GitFixture.Run(remote, "commit", "-q", "-m", "feature");
        var headSha = GitFixture.Run(remote, "rev-parse", "HEAD");
        GitFixture.Run(remote, "checkout", "-q", "main");

        SeedDefaultBranchIndex(baseSha);
        var defaultNodesBefore = _graph.Nodes.ToList();
        var defaultEdgesBefore = _graph.Edges.ToList();
        var cacheHeadBefore = GitFixture.Run(cache, "rev-parse", "HEAD");
        var service = CreateService(cache);

        var overlay = await service.CreateOverlayAsync("Michael", Project, "factory/feature");
        (await service.GetOverlayAsync(overlay.Id))!.Status.ShouldBe(BranchOverlayStatuses.Queued);

        await service.IndexOverlayAsync(overlay.Id);

        var status = await service.GetOverlayAsync(overlay.Id);
        status.ShouldNotBeNull();
        status.Status.ShouldBe(BranchOverlayStatuses.Ready);
        status.HeadSha.ShouldBe(headSha);
        status.BaseSha.ShouldBe(baseSha);
        status.Ref.ShouldBe("factory/feature");
        status.ChangedFileCount.ShouldBe(5);

        var files = (await _overlays.GetOverlayFilesAsync(overlay.Id))
            .Select(file => (file.FilePath, file.ChangeKind, file.PreviousPath))
            .OrderBy(file => file.FilePath)
            .ToList();
        files.ShouldBe(
        [
            ("src/a.py", BranchOverlayChangeKinds.Modified, null),
            ("src/c.py", BranchOverlayChangeKinds.Deleted, null),
            ("src/d.py", BranchOverlayChangeKinds.Deleted, null),
            ("src/e.py", BranchOverlayChangeKinds.Added, null),
            ("src/f.py", BranchOverlayChangeKinds.Renamed, "src/d.py")
        ]);

        var nodes = await _overlays.GetOverlayNodesAsync(overlay.Id);
        nodes.Select(node => node.FilePath).Distinct().OrderBy(path => path)
            .ShouldBe(["src/a.py", "src/e.py", "src/f.py"]);
        nodes.Where(node => node.Label == NodeLabel.Function).Select(node => node.Name).OrderBy(name => name)
            .ShouldBe(["alpha", "alpha_two", "delta", "epsilon"]);
        nodes.Count(node => node.Label == NodeLabel.File).ShouldBe(3);
        status.NodeCount.ShouldBe(nodes.Count);

        // Calls into unchanged files resolve against the persisted default-branch graph.
        var edges = await _overlays.GetOverlayEdgesAsync(overlay.Id);
        edges.ShouldContain(edge => edge.Type == EdgeType.CALLS
                                    && edge.SourceQN == $"{Project}:src/e.py:epsilon"
                                    && edge.TargetQN == $"{Project}:src/b.py:beta");

        _graph.Nodes.ShouldBe(defaultNodesBefore);
        _graph.Edges.ShouldBe(defaultEdgesBefore);
        GitFixture.Run(cache, "rev-parse", "HEAD").ShouldBe(cacheHeadBefore);
        GitFixture.Run(cache, "status", "--porcelain").ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexOverlayAsync_CommitShaInCheckoutWithoutRemote_ResolvesLocally()
    {
        var (remote, _, baseSha) = CreateRemoteAndSingleBranchCache();
        GitFixture.Run(remote, "checkout", "-q", "-b", "topic");
        GitFixture.Write(remote, "src/b.py", "def beta\ndef beta_two\n");
        GitFixture.Run(remote, "commit", "-q", "-am", "topic");
        var headSha = GitFixture.Run(remote, "rev-parse", "HEAD");
        GitFixture.Run(remote, "checkout", "-q", "main");
        SeedDefaultBranchIndex(baseSha);
        var service = CreateService(remote);

        var overlay = await service.CreateOverlayAsync("Michael", Project, headSha);
        await service.IndexOverlayAsync(overlay.Id);

        var status = await service.GetOverlayAsync(overlay.Id);
        status!.Status.ShouldBe(BranchOverlayStatuses.Ready);
        status.HeadSha.ShouldBe(headSha);
        status.BaseSha.ShouldBe(baseSha);
        (await _overlays.GetOverlayFilesAsync(overlay.Id)).Single().ChangeKind
            .ShouldBe(BranchOverlayChangeKinds.Modified);
    }

    [Fact]
    public async Task IndexOverlayAsync_UnknownRef_ReportsFailedWithoutReplay()
    {
        var (_, cache, baseSha) = CreateRemoteAndSingleBranchCache();
        SeedDefaultBranchIndex(baseSha);
        var service = CreateService(cache);

        var overlay = await service.CreateOverlayAsync("Michael", Project, "factory/missing");
        await Should.ThrowAsync<BranchOverlayIndexingException>(() => service.IndexOverlayAsync(overlay.Id));

        var status = await service.GetOverlayAsync(overlay.Id);
        status!.Status.ShouldBe(BranchOverlayStatuses.Failed);
        status.Error.ShouldNotBeNullOrWhiteSpace();
        status.BaseSha.ShouldBe(baseSha);
    }

    [Fact]
    public async Task CreateOverlayAsync_RejectsUnknownRepositoryAndOptionLikeRefs()
    {
        var service = CreateService(_root);
        _graph.AddProject(Project);

        await Should.ThrowAsync<KeyNotFoundException>(() => service.CreateOverlayAsync("u", "Nope", "main"));
        await Should.ThrowAsync<ArgumentException>(() => service.CreateOverlayAsync("u", Project, "--upload-pack=evil"));
        await Should.ThrowAsync<ArgumentException>(() => service.CreateOverlayAsync("u", Project, "main..other"));
    }

    [Fact]
    public async Task TtlSweep_DeletesOverlayDataAndStatusAnswersExpired()
    {
        _graph.AddProject(Project);
        var service = CreateService(_root, ttlHours: 24);
        var old = await service.CreateOverlayAsync("u", Project, "factory/old");
        await _overlays.CompleteOverlayAsync(
            old.Id,
            new string('a', 40),
            new string('b', 40),
            [new BranchOverlayFileEntity { OverlayId = old.Id, FilePath = "src/a.py", ChangeKind = BranchOverlayChangeKinds.Modified }],
            [new GraphNode { Project = Project, Label = NodeLabel.Function, Name = "alpha", QualifiedName = "q:alpha", FilePath = "src/a.py" }],
            [new PendingEdge("q:alpha", "q:beta", EdgeType.CALLS)],
            _time.GetUtcNow().UtcDateTime);

        _time.Now += TimeSpan.FromHours(23);
        var fresh = await service.CreateOverlayAsync("u", Project, "factory/fresh");
        (await service.ExpireOverlaysAsync()).ShouldBeEmpty();
        (await service.GetOverlayAsync(old.Id))!.Status.ShouldBe(BranchOverlayStatuses.Ready);

        _time.Now += TimeSpan.FromHours(1);
        using var provider = new ServiceCollection()
            .AddSingleton<IBranchOverlayService>(service)
            .BuildServiceProvider();
        var worker = new BranchOverlayExpiryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BranchOverlayOptions { TtlHours = 24 }),
            NullLogger<BranchOverlayExpiryWorker>.Instance);
        await worker.SweepOnceAsync(CancellationToken.None);

        var expired = await service.GetOverlayAsync(old.Id);
        expired!.Status.ShouldBe(BranchOverlayStatuses.Expired);
        expired.ExpiredAt.ShouldNotBeNull();
        expired.HeadSha.ShouldBe(new string('a', 40));
        (await _overlays.GetOverlayFilesAsync(old.Id)).ShouldBeEmpty();
        (await _overlays.GetOverlayNodesAsync(old.Id)).ShouldBeEmpty();
        (await _overlays.GetOverlayEdgesAsync(old.Id)).ShouldBeEmpty();
        (await service.GetOverlayAsync(fresh.Id))!.Status.ShouldBe(BranchOverlayStatuses.Queued);

        // Status reads expire an overlay past its TTL even before the next sweep runs.
        _time.Now += TimeSpan.FromHours(23);
        (await service.GetOverlayAsync(fresh.Id))!.Status.ShouldBe(BranchOverlayStatuses.Expired);
        (await service.IndexOverlayAsync(fresh.Id)).ShouldContain("expired");
    }

    [Fact]
    public void ParseNameStatus_ParsesRenamesCopiesAndNulSeparatedPaths()
    {
        var output = Encoding.UTF8.GetBytes(
            "M\0src/a b.py\0A\0src/new.py\0D\0src/gone.py\0R087\0src/old.py\0src/moved.py\0C100\0src/x.py\0src/y.py\0T\0src/link.py\0");

        BranchOverlayGit.ParseNameStatus(output).ShouldBe(
        [
            new OverlayGitChange(OverlayGitChangeKind.Modified, "src/a b.py"),
            new OverlayGitChange(OverlayGitChangeKind.Added, "src/new.py"),
            new OverlayGitChange(OverlayGitChangeKind.Deleted, "src/gone.py"),
            new OverlayGitChange(OverlayGitChangeKind.Renamed, "src/moved.py", "src/old.py"),
            new OverlayGitChange(OverlayGitChangeKind.Added, "src/y.py"),
            new OverlayGitChange(OverlayGitChangeKind.Modified, "src/link.py")
        ]);
    }

    private (string Remote, string Cache, string BaseSha) CreateRemoteAndSingleBranchCache()
    {
        var remote = Path.Combine(_root, "remote", Project);
        Directory.CreateDirectory(remote);
        GitFixture.Run(remote, "init", "-q", "-b", "main");
        GitFixture.Run(remote, "config", "commit.gpgsign", "false");
        GitFixture.Write(remote, "src/a.py", "def alpha\ncall beta\n");
        GitFixture.Write(remote, "src/b.py", "def beta\n");
        GitFixture.Write(remote, "src/c.py", "def gamma\n");
        GitFixture.Write(remote, "src/d.py", "def delta\nline one\nline two\nline three\n");
        GitFixture.Write(remote, "README.md", "docs\n");
        GitFixture.Run(remote, "add", "-A");
        GitFixture.Run(remote, "commit", "-q", "-m", "base");
        var baseSha = GitFixture.Run(remote, "rev-parse", "HEAD");

        var cache = Path.Combine(_root, "cache", Project);
        GitFixture.Run(_root, "clone", "-q", "--single-branch", remote, cache);
        return (remote, cache, baseSha);
    }

    private void SeedDefaultBranchIndex(string baseSha)
    {
        _graph.AddProject(Project);
        _graph.UpsertSyncStateAsync(new SyncStateEntity { Project = Project, LastCommitSha = baseSha });
        foreach (var (path, name) in new[] { ("src/a.py", "alpha"), ("src/b.py", "beta"), ("src/c.py", "gamma"), ("src/d.py", "delta") })
        {
            _graph.AddNode(new GraphNode
            {
                Project = Project,
                Label = NodeLabel.Function,
                Name = name,
                QualifiedName = $"{Project}:{path}:{name}",
                FilePath = path
            });
        }
    }

    private BranchOverlayService CreateService(string checkoutPath, double ttlHours = 72)
    {
        var pipeline = new IndexingPipeline(
            _graph,
            [new LineFunctionExtractor()],
            Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance);

        return new BranchOverlayService(
            _overlays,
            _graph,
            new FixedPathRepoProvider(checkoutPath),
            pipeline,
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions { TtlHours = ttlHours }),
            NullLogger<BranchOverlayService>.Instance,
            _time);
    }
}
