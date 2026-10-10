using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// The request_overlay / get_overlay_status MCP tools against the real standalone indexer
/// operations, branch overlay service, and git (a local "origin" remote plus a single-branch cache).
/// </summary>
public sealed class OverlayMcpServerTests : IDisposable
{
    private const string Project = "OverlayRepo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-overlay-mcp-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _graph = new();
    private readonly InMemoryBranchOverlayStore _overlays = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly StandaloneIndexerOperationsServiceTests.RecordingBackgroundRunner _runner = new();
    private readonly StandaloneIndexerOperationsServiceTests.FakeIndexerRunStore _runs = new();
    private readonly string _remote;
    private readonly string _baseSha;
    private readonly BranchOverlayService _service;
    private readonly OverlayMcpServer _tools;

    public OverlayMcpServerTests()
    {
        _remote = Path.Combine(_root, "remote", Project);
        Directory.CreateDirectory(_remote);
        GitFixture.Run(_remote, "init", "-q", "-b", "main");
        GitFixture.Run(_remote, "config", "commit.gpgsign", "false");
        GitFixture.Write(_remote, "src/a.py", "def alpha\n");
        GitFixture.Write(_remote, "src/b.py", "def beta\n");
        GitFixture.Run(_remote, "add", "-A");
        GitFixture.Run(_remote, "commit", "-q", "-m", "base");
        _baseSha = GitFixture.Run(_remote, "rev-parse", "HEAD");

        var cacheRoot = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cacheRoot);
        GitFixture.Run(cacheRoot, "clone", "-q", "--single-branch", _remote, Path.Combine(cacheRoot, Project));

        _graph.AddProject(Project);
        _graph.UpsertSyncStateAsync(new SyncStateEntity { Project = Project, LastCommitSha = _baseSha });

        var sourceOptions = Options.Create(new RepositorySourceOptions { ReposCachePath = cacheRoot });
        _service = new BranchOverlayService(
            _overlays,
            _graph,
            new FixedPathRepoProvider(Path.Combine(cacheRoot, Project)),
            new IndexingPipeline(_graph, [new LineFunctionExtractor()], Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance),
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions { TtlHours = 72 }),
            NullLogger<BranchOverlayService>.Instance,
            _time,
            sourceOptions);
        var operations = new StandaloneIndexerOperationsService(
            _runs,
            new StandaloneIndexerOperationsServiceTests.FakeDatabaseSourceStore(),
            _runner,
            _service);
        _tools = new OverlayMcpServer(
            operations,
            new HttpContextAccessor(),
            new CommitScopeResolver(_graph, sourceOptions, _overlays, timeProvider: _time));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task RequestOverlay_BranchName_CreatesQueuedOverlayPinnedToBranchHead()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");

        var result = await _tools.RequestOverlay(Project, "factory/feature");

        result.IsError.ShouldNotBe(true);
        var structured = Structured(result);
        var overlayId = structured.GetProperty("overlayId").GetInt64();
        overlayId.ShouldBeGreaterThan(0);
        structured.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Queued);
        structured.GetProperty("headSha").GetString().ShouldBe(headSha);
        structured.GetProperty("commitSha").GetString().ShouldBe(headSha);
        structured.GetProperty("baseSha").GetString().ShouldBe(_baseSha);
        structured.GetProperty("reused").GetBoolean().ShouldBeFalse();
        Text(result).ShouldStartWith($"Commit: {headSha}\n");
        _runner.EnqueuedRunIds.Count.ShouldBe(1);
        (await _overlays.GetOverlayAsync(overlayId))!.IndexerRunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task RequestOverlay_Repeat_ReturnsTheSameLiveOverlay()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");

        var first = Structured(await _tools.RequestOverlay(Project, "factory/feature"));
        var again = Structured(await _tools.RequestOverlay(Project, "factory/feature"));
        var bySha = Structured(await _tools.RequestOverlay(Project, headSha.ToUpperInvariant()));

        again.GetProperty("overlayId").GetInt64().ShouldBe(first.GetProperty("overlayId").GetInt64());
        again.GetProperty("reused").GetBoolean().ShouldBeTrue();
        bySha.GetProperty("overlayId").GetInt64().ShouldBe(first.GetProperty("overlayId").GetInt64());
        bySha.GetProperty("reused").GetBoolean().ShouldBeTrue();
        _overlays.All.Count.ShouldBe(1);
        _runner.EnqueuedRunIds.Count.ShouldBe(1);

        // Once indexed, the ready overlay is still the live one for that commit.
        await _service.IndexOverlayAsync(first.GetProperty("overlayId").GetInt64());
        var ready = Structured(await _tools.RequestOverlay(Project, "factory/feature"));
        ready.GetProperty("overlayId").GetInt64().ShouldBe(first.GetProperty("overlayId").GetInt64());
        ready.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Ready);
        ready.GetProperty("reused").GetBoolean().ShouldBeTrue();
        _overlays.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestOverlay_ConcurrentIdenticalRequests_CreateOneOverlay()
    {
        CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _tools.RequestOverlay(Project, "factory/feature")));

        results.Select(result => Structured(result).GetProperty("overlayId").GetInt64()).Distinct().Count().ShouldBe(1);
        results.Count(result => !Structured(result).GetProperty("reused").GetBoolean()).ShouldBe(1);
        _overlays.All.Count.ShouldBe(1);
        _runner.EnqueuedRunIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestOverlay_BranchMovedOn_CreatesOverlayForTheNewHead()
    {
        var firstHead = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");
        var first = Structured(await _tools.RequestOverlay(Project, "factory/feature"));

        var secondHead = CommitOnBranch("factory/feature", "src/b.py", "def beta\ndef beta_two\n");
        var second = Structured(await _tools.RequestOverlay(Project, "factory/feature"));

        second.GetProperty("overlayId").GetInt64().ShouldNotBe(first.GetProperty("overlayId").GetInt64());
        second.GetProperty("headSha").GetString().ShouldBe(secondHead);
        second.GetProperty("reused").GetBoolean().ShouldBeFalse();

        // The first overlay stays pinned to the head its request answered with.
        await _service.IndexOverlayAsync(first.GetProperty("overlayId").GetInt64());
        var status = Structured(await _tools.GetOverlayStatus(first.GetProperty("overlayId").GetInt64()));
        status.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Ready);
        status.GetProperty("headSha").GetString().ShouldBe(firstHead);
    }

    [Fact]
    public async Task RequestOverlay_FailedExpiredOrStaleOverlay_IsReplacedByANewOne()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");
        var failedId = Structured(await _tools.RequestOverlay(Project, headSha)).GetProperty("overlayId").GetInt64();
        await _overlays.MarkOverlayFailedAsync(failedId, "boom", null, null, _time.GetUtcNow().UtcDateTime);

        var afterFailure = Structured(await _tools.RequestOverlay(Project, headSha));
        var readyId = afterFailure.GetProperty("overlayId").GetInt64();
        readyId.ShouldNotBe(failedId);
        afterFailure.GetProperty("reused").GetBoolean().ShouldBeFalse();

        // Stale: the default-branch index moved past the ready overlay's base.
        await _service.IndexOverlayAsync(readyId);
        var newDefault = CommitOnBranch("main", "src/b.py", "def beta\ndef beta_main\n");
        await _graph.UpsertSyncStateAsync(new SyncStateEntity { Project = Project, LastCommitSha = newDefault });
        var staleStatus = Structured(await _tools.GetOverlayStatus(readyId));
        staleStatus.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Ready);
        staleStatus.GetProperty("stale").GetBoolean().ShouldBeTrue();

        var afterStale = Structured(await _tools.RequestOverlay(Project, headSha));
        var freshId = afterStale.GetProperty("overlayId").GetInt64();
        freshId.ShouldNotBe(readyId);
        afterStale.GetProperty("baseSha").GetString().ShouldBe(newDefault);

        // Expired: past the TTL the overlay is expired and a new request queues another.
        _time.Now = _time.Now.AddHours(73);
        var expired = await _tools.GetOverlayStatus(freshId);
        Structured(expired).GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Expired);
        Text(expired).ShouldContain("Expired");
        var afterExpiry = Structured(await _tools.RequestOverlay(Project, headSha));
        afterExpiry.GetProperty("overlayId").GetInt64().ShouldNotBe(freshId);
        afterExpiry.GetProperty("reused").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task RequestOverlay_RunCannotBeQueued_MarksOverlayFailedAndNextRequestQueuesANewOne()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");
        _runs.CreateFailure = new InvalidOperationException("run store unavailable");

        await Should.ThrowAsync<InvalidOperationException>(() => _tools.RequestOverlay(Project, "factory/feature"));

        var orphan = _overlays.All.ShouldHaveSingleItem();
        orphan.Status.ShouldBe(BranchOverlayStatuses.Failed);
        orphan.Error.ShouldBe("Run could not be queued: run store unavailable");
        orphan.HeadSha.ShouldBe(headSha);

        _runs.CreateFailure = null;
        using var cts = new CancellationTokenSource();
        var next = Structured(await _tools.RequestOverlay(Project, "factory/feature", cts.Token));
        next.GetProperty("overlayId").GetInt64().ShouldNotBe(orphan.Id);
        next.GetProperty("reused").GetBoolean().ShouldBeFalse();
        next.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Queued);
        // Queuing the run is not abandoned when the caller's request is aborted.
        _runs.LastCreateToken.ShouldNotBeNull().CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestOverlay_QueuedOverlayWithoutRun_IsReusedOnlyWithinTheGracePeriod()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");
        var now = _time.GetUtcNow().UtcDateTime;
        var orphanId = await _overlays.CreateOverlayAsync(new BranchOverlayEntity
        {
            Project = Project,
            RequestedRef = "factory/feature",
            HeadSha = headSha,
            BaseSha = _baseSha,
            Status = BranchOverlayStatuses.Queued,
            CreatedAt = now - BranchOverlayService.UnqueuedOverlayGrace + TimeSpan.FromSeconds(1)
        });

        // Still within the grace period: the request that recorded it may be queuing its run.
        var within = Structured(await _tools.RequestOverlay(Project, "factory/feature"));
        within.GetProperty("overlayId").GetInt64().ShouldBe(orphanId);
        within.GetProperty("reused").GetBoolean().ShouldBeTrue();

        _time.Now = _time.Now.AddSeconds(2);
        var after = Structured(await _tools.RequestOverlay(Project, "factory/feature"));
        after.GetProperty("overlayId").GetInt64().ShouldNotBe(orphanId);
        after.GetProperty("reused").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task GetOverlayStatus_ReportsQueuedReadyAndFailed()
    {
        var headSha = CommitOnBranch("factory/feature", "src/a.py", "def alpha\ndef alpha_two\n");
        var id = Structured(await _tools.RequestOverlay(Project, "factory/feature")).GetProperty("overlayId").GetInt64();

        var queued = await _tools.GetOverlayStatus(id);
        var queuedStructured = Structured(queued);
        queuedStructured.GetProperty("overlayId").GetInt64().ShouldBe(id);
        queuedStructured.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Queued);
        queuedStructured.GetProperty("headSha").GetString().ShouldBe(headSha);
        queuedStructured.GetProperty("baseSha").GetString().ShouldBe(_baseSha);
        queuedStructured.TryGetProperty("reused", out _).ShouldBeFalse();
        Text(queued).ShouldStartWith($"Commit: {headSha}\n");

        await _service.IndexOverlayAsync(id);
        var ready = Structured(await _tools.GetOverlayStatus(id));
        ready.GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Ready);
        ready.GetProperty("stale").GetBoolean().ShouldBeFalse();
        ready.GetProperty("changedFileCount").GetInt32().ShouldBe(1);

        var otherId = Structured(await _tools.RequestOverlay(Project, CommitOnBranch("factory/other", "src/b.py", "def beta\ndef b2\n")))
            .GetProperty("overlayId").GetInt64();
        await _overlays.MarkOverlayFailedAsync(otherId, "extraction failed", null, null, _time.GetUtcNow().UtcDateTime);
        var failed = await _tools.GetOverlayStatus(otherId);
        Structured(failed).GetProperty("status").GetString().ShouldBe(BranchOverlayStatuses.Failed);
        Structured(failed).GetProperty("overlayError").GetString().ShouldBe("extraction failed");
        failed.IsError.ShouldNotBe(true);
    }

    [Fact]
    public async Task UnknownRepoRefAndOverlay_AnswerTypedErrors()
    {
        AssertError(await _tools.RequestOverlay("NoSuchRepo", "main"), OverlayMcpServer.RepoNotFoundCode);
        AssertError(await _tools.RequestOverlay(Project, "factory/does-not-exist"), OverlayMcpServer.InvalidRefCode);
        AssertError(await _tools.RequestOverlay(Project, "main~1"), OverlayMcpServer.InvalidRefCode);
        AssertError(await _tools.RequestOverlay(Project, " "), OverlayMcpServer.InvalidRefCode);
        AssertError(await _tools.GetOverlayStatus(9999), OverlayMcpServer.OverlayNotFoundCode);
        AssertError(await _tools.GetOverlayStatus(0), OverlayMcpServer.InvalidRequestCode);
        _overlays.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequestOverlay_AnnotatedTag_ResolvesToThePeeledCommit()
    {
        var headSha = CommitOnBranch("release", "src/a.py", "def alpha\ndef tagged\n");
        GitFixture.Run(_remote, "tag", "-a", "v1.0", "-m", "v1.0", headSha);

        var structured = Structured(await _tools.RequestOverlay(Project, "v1.0"));

        structured.GetProperty("headSha").GetString().ShouldBe(headSha);
    }

    private string CommitOnBranch(string branch, string path, string content)
    {
        var exists = GitFixture.Run(_remote, "branch", "--list", branch).Length > 0;
        GitFixture.Run(_remote, exists ? ["checkout", "-q", branch] : ["checkout", "-q", "-b", branch]);
        GitFixture.Write(_remote, path, content);
        GitFixture.Run(_remote, "commit", "-q", "-am", $"{branch} change");
        var sha = GitFixture.Run(_remote, "rev-parse", "HEAD");
        GitFixture.Run(_remote, "checkout", "-q", "main");
        return sha;
    }

    private static void AssertError(CallToolResult result, string code)
    {
        result.IsError.ShouldBe(true);
        Structured(result).GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        Text(result).ShouldStartWith($"Error ({code})");
    }

    private static JsonElement Structured(CallToolResult result)
        => result.StructuredContent.ShouldNotBeNull().ShouldBeOfType<JsonElement>();

    private static string Text(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().Single().Text;
}
