using System.Text.Json;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// REST and MCP read surfaces answering from a branch overlay (sha) over the default-branch graph.
/// Fixture: a git repo whose default branch (base) has src/a.py, src/b.py, src/d.py; the branch
/// head modifies src/a.py (alpha moves, gone is removed, added_fn is added), deletes src/d.py and
/// adds src/n.py. The checkout's working tree stays at the base commit.
/// </summary>
public sealed class OverlayQueryTests : IDisposable
{
    private const string Repo = "Repo";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-overlay-query-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();
    private readonly InMemoryBranchOverlayStore _overlays = new();
    private readonly MutableTimeProvider _time = new(Now);
    private readonly string _baseSha;
    private readonly string _headSha;
    private readonly long _alphaId;
    private readonly long _goneId;
    private readonly long _betaId;
    private readonly long _deltaId;

    public OverlayQueryTests()
    {
        Directory.CreateDirectory(_root);
        GitFixture.Run(_root, "init", "-q");
        GitFixture.Write(_root, "src/a.py", "def alpha():\n    return 'base alpha'\n\ndef gone():\n    return 1\n");
        GitFixture.Write(_root, "src/b.py", "def beta():\n    alpha()\n    gone()\n    delta()\n");
        GitFixture.Write(_root, "src/d.py", "def delta():\n    return 'delta'\n");
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "base");
        _baseSha = GitFixture.Run(_root, "rev-parse", "HEAD");

        GitFixture.Write(_root, "src/a.py", "# head\n\ndef added_fn():\n    return 2\n\ndef alpha():\n    return 'head alpha'\n");
        File.Delete(Path.Combine(_root, "src/d.py"));
        GitFixture.Write(_root, "src/n.py", "def newbie():\n    alpha()\n");
        GitFixture.Run(_root, "add", "-A");
        GitFixture.Run(_root, "commit", "-q", "-m", "head");
        _headSha = GitFixture.Run(_root, "rev-parse", "HEAD");
        // The overlay fetch leaves the head reachable from refs/codegraph/overlay-head while the
        // default-branch checkout stays at the base commit.
        GitFixture.Run(_root, "update-ref", "refs/codegraph/overlay-head", _headSha);
        GitFixture.Run(_root, "reset", "-q", "--hard", _baseSha);

        _store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = Repo,
            LocalPath = _root,
            LastCommitSha = _baseSha,
            RepoUrl = "https://github.com/example/repo.git"
        }).GetAwaiter().GetResult();

        _alphaId = _store.AddNode(Function("alpha", "src/a.py", 1, 2));
        _goneId = _store.AddNode(Function("gone", "src/a.py", 4, 5));
        _betaId = _store.AddNode(Function("beta", "src/b.py", 1, 4));
        _deltaId = _store.AddNode(Function("delta", "src/d.py", 1, 2));
        _store.AddEdge(Calls(_betaId, _alphaId));
        _store.AddEdge(Calls(_betaId, _goneId));
        _store.AddEdge(Calls(_betaId, _deltaId));
    }

    // ── Graph merge ──────────────────────────────────────────────────────

    [Fact]
    public async Task OverlayView_ReplacesChangedFiles_RetargetsInboundEdges_AndLeavesOutDeletedFiles()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var scope = await Resolver().ResolveAsync(Repo, _headSha);
        var graph = scope.Graph;

        scope.NotIndexed.ShouldBeNull();
        scope.Overlay.ShouldNotBeNull();
        (await graph.FindNodesByFileAsync(Repo, "src/a.py")).Select(node => node.Name)
            .ShouldBe(["added_fn", "alpha"], ignoreOrder: true);
        (await graph.FindNodesByFileAsync(Repo, "src/d.py")).ShouldBeEmpty();
        (await graph.FindNodesByFileAsync(Repo, "src/b.py")).ShouldHaveSingleItem().Id.ShouldBe(_betaId);

        var headAlpha = (await graph.FindNodeByQualifiedNameAsync(Repo, Qn("src/a.py", "alpha")))!;
        headAlpha.Id.ShouldBeLessThan(0);
        headAlpha.StartLine.ShouldBe(6);
        // A default-branch id hidden by the overlay resolves to the head node with its qualified name.
        (await graph.FindNodeByIdAsync(_alphaId))!.Id.ShouldBe(headAlpha.Id);
        (await graph.FindNodeByIdAsync(_goneId)).ShouldBeNull();
        (await graph.FindNodeByIdAsync(_deltaId)).ShouldBeNull();

        // Unchanged beta -> alpha is retargeted to the head alpha; beta -> gone and beta -> delta are dropped.
        (await graph.FindEdgesBySourceAsync(_betaId)).Select(edge => edge.TargetId).ShouldBe([headAlpha.Id]);
        var callers = await graph.TraverseAsync(headAlpha.Id, TraceDirection.Inbound, 2, [EdgeType.CALLS]);
        callers.Select(entry => entry.Node.Name).ShouldBe(["beta", "newbie"], ignoreOrder: true);

        var search = await graph.SearchNodesAsync(Repo, "%");
        search.Select(node => node.Name).ShouldBe(["added_fn", "alpha", "beta", "newbie"], ignoreOrder: true);
        (await graph.SearchNodesCountAsync(Repo, "%")).ShouldBe(4);
    }

    // ── AC1: head version and its impact with sha, default branch without ─────────

    [Fact]
    public async Task GetCodeSnippet_ReturnsHeadVersionWithSha_AndDefaultBranchVersionWithout()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();

        var head = await mcp.GetCodeSnippet(Repo, "src/a.py", sha: _headSha);
        var defaultBranch = await mcp.GetCodeSnippet(Repo, "src/a.py");

        FirstLine(head).ShouldBe($"Commit: {_headSha}");
        head.StructuredString("commitSha").ShouldBe(_headSha);
        head.StructuredString("source").ShouldBe("overlay");
        head.Text().ShouldContain("head alpha");
        head.Text().ShouldNotContain("base alpha");

        FirstLine(defaultBranch).ShouldBe($"Commit: {_baseSha}");
        defaultBranch.StructuredString("commitSha").ShouldBe(_baseSha);
        defaultBranch.Text().ShouldContain("base alpha");
        defaultBranch.Text().ShouldNotContain("head alpha");
    }

    [Fact]
    public async Task GetCodeSnippet_WithSha_ReadsTheCommitNotTheWorkingTree()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        // An unchanged file edited in the working tree: the sha answer reads the committed blob.
        GitFixture.Write(_root, "src/b.py", "uncommitted edit\n");

        var answer = await Mcp().GetCodeSnippet(Repo, "src/b.py", sha: _headSha);

        answer.Text().ShouldContain("def beta():");
        answer.Text().ShouldNotContain("uncommitted edit");
    }

    [Fact]
    public async Task FileImpact_Rest_WithSha_ReturnsHeadNodesAndTheirDependents()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var controller = ProjectsController();

        var head = (ImpactReport)((OkObjectResult)(await controller.FileImpact(Repo, "src/a.py", sha: _headSha)).Result!).Value!;
        var defaultBranch = (ImpactReport)((OkObjectResult)(await controller.FileImpact(Repo, "src/a.py")).Result!).Value!;

        head.CommitSha.ShouldBe(_headSha);
        head.ChangedNodes.Select(node => node.Name).ShouldBe(["added_fn", "alpha"], ignoreOrder: true);
        head.AffectedNodes.Select(node => node.Name).ShouldBe(["beta", "newbie"], ignoreOrder: true);

        defaultBranch.CommitSha.ShouldBe(_baseSha);
        defaultBranch.ChangedNodes.Select(node => node.Name).ShouldBe(["alpha", "gone"], ignoreOrder: true);
    }

    [Fact]
    public async Task Impact_RestAndMcp_WithSha_AnswerFromOverlay()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);

        var rest = (ImpactReport)((OkObjectResult)(await ProjectsController().Impact(Repo, Qn("src/a.py", "alpha"), sha: _headSha)).Result!).Value!;
        rest.CommitSha.ShouldBe(_headSha);
        rest.ChangedNodes.ShouldHaveSingleItem().NodeId.ShouldBeLessThan(0);
        rest.AffectedNodes.Select(node => node.Name).ShouldBe(["beta", "newbie"], ignoreOrder: true);

        var mcp = await Mcp().AnalyzeImpact(Qn("src/a.py", "alpha"), Repo, sha: _headSha);
        FirstLine(mcp).ShouldBe($"Commit: {_headSha}");
        mcp.StructuredString("commitSha").ShouldBe(_headSha);
        mcp.Text().ShouldContain("newbie");

        var removed = await Mcp().AnalyzeImpact(Qn("src/a.py", "gone"), Repo, sha: _headSha);
        removed.Body().ShouldStartWith("No nodes found");
    }

    [Fact]
    public async Task SearchGraph_WithSha_DoesNotLabelChangedFilesWithDefaultBranchTrust()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        await _store.UpsertFileMetricsBatchAsync(Repo,
        [
            new FileMetricsEntity { Project = Repo, FilePath = "src/a.py", TrustScore = 0.1 },
            new FileMetricsEntity { Project = Repo, FilePath = "src/b.py", TrustScore = 0.1 }
        ]);

        var head = (await Mcp().SearchGraph("%", project: Repo, sha: _headSha)).Text();
        var defaultBranch = (await Mcp().SearchGraph("alpha", project: Repo)).Text();

        // src/a.py changed at the head: its default-branch metrics do not describe it.
        head.ShouldContain($"- **alpha** (Function) — {Repo}\n");
        // src/b.py is unchanged at the head, so its default-branch metrics still apply.
        head.ShouldContain($"- **beta** (Function) — {Repo} _(low trust)_");
        defaultBranch.ShouldContain($"- **alpha** (Function) — {Repo} _(low trust)_");
    }

    // ── Deleted files ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeletedFile_IsLeftOutOfEverySurface_WithSha()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();

        var snippet = await mcp.GetCodeSnippet(Repo, "src/d.py", sha: _headSha);
        snippet.StructuredString("commitSha").ShouldBe(_headSha);
        snippet.Body().ShouldContain("was deleted");
        snippet.Text().ShouldNotContain("delta'");

        (await mcp.ReadNodeSource(_deltaId, _headSha)).Body().ShouldBe($"Node {_deltaId} not found at commit {_headSha}.");
        (await mcp.SearchGraph("delta", project: Repo, sha: _headSha)).Body().ShouldStartWith("No results found");
        (await NodesController().ByFile(Repo, "src/d.py", sha: _headSha)).Result.ShouldBeOfType<NotFoundObjectResult>();
        (await ProjectsController().FileImpact(Repo, "src/d.py", sha: _headSha)).Result.ShouldBeOfType<NotFoundResult>();
        (await NodesController().Source(_deltaId, _headSha)).Result.ShouldBeOfType<NotFoundResult>();

        // Without sha the default branch still has it.
        (await mcp.GetCodeSnippet(Repo, "src/d.py")).Text().ShouldContain("delta");
    }

    // ── AC2: not-ready SHA ───────────────────────────────────────────────

    [Theory]
    [InlineData(BranchOverlayStatuses.Queued)]
    [InlineData(BranchOverlayStatuses.Indexing)]
    [InlineData(BranchOverlayStatuses.Failed)]
    public async Task NotReadySha_ReturnsTypedNotIndexed_WithOverlayStatus(string status)
    {
        var id = await _overlays.CreateOverlayAsync(new BranchOverlayEntity
        {
            Project = Repo,
            RequestedRef = _headSha,
            CreatedAt = Now.UtcDateTime
        });
        if (status != BranchOverlayStatuses.Queued)
            await _overlays.MarkOverlayIndexingAsync(id, Now.UtcDateTime);
        if (status == BranchOverlayStatuses.Failed)
            await _overlays.MarkOverlayFailedAsync(id, "boom", _headSha, _baseSha, Now.UtcDateTime);

        var answer = await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _headSha);

        AssertNotIndexed(answer, _headSha, status);
        answer.Text().ShouldNotContain("alpha");

        var rest = await ProjectsController().FileImpact(Repo, "src/a.py", sha: _headSha);
        var conflict = rest.Result.ShouldBeOfType<ConflictObjectResult>();
        var body = conflict.Value.ShouldBeOfType<CommitNotIndexedResponse>();
        body.Code.ShouldBe(CommitScopeResolver.NotIndexedCode);
        body.Status.ShouldBe(status);
        body.Sha.ShouldBe(_headSha);
        body.OverlayId.ShouldBe(id);
    }

    [Fact]
    public async Task UnknownSha_ReturnsNotIndexedUnknown_OnEverySurface()
    {
        var unknown = new string('c', 40);
        var mcp = Mcp();

        AssertNotIndexed(await mcp.SearchGraph("alpha", project: Repo, sha: unknown), unknown, "unknown");
        AssertNotIndexed(await mcp.TraceCallPath("alpha", project: Repo, sha: unknown), unknown, "unknown");
        AssertNotIndexed(await mcp.ReadNodeSource(_alphaId, unknown), unknown, "unknown");
        AssertNotIndexed(await mcp.AnalyzeImpact("alpha", Repo, sha: unknown), unknown, "unknown");

        var search = (await NodesController().Search("alpha", Repo, sha: unknown)).Result.ShouldBeOfType<ConflictObjectResult>();
        search.Value.ShouldBeOfType<CommitNotIndexedResponse>().Status.ShouldBe("unknown");
        (await NodesController().Source(_alphaId, unknown)).Result.ShouldBeOfType<ConflictObjectResult>();
        (await NodesController().ByFile(Repo, "src/a.py", sha: unknown)).Result.ShouldBeOfType<ConflictObjectResult>();
        (await ProjectsController().Impact(Repo, "alpha", sha: unknown)).Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task ExpiredOverlay_PastTtl_ReportsExpired()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        _time.Now = Now.AddHours(new BranchOverlayOptions().TtlHours + 1);

        AssertNotIndexed(await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _headSha), _headSha, BranchOverlayStatuses.Expired);
    }

    [Fact]
    public async Task OverlayBuiltOnAnOlderDefaultCommit_IsStale_AndNeverAnswered()
    {
        await CreateReadyOverlayAsync(_headSha, new string('e', 40));

        var answer = await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _headSha);

        AssertNotIndexed(answer, _headSha, CommitScopeResolver.StaleStatus);
        answer.Text().ShouldNotContain("head alpha");
    }

    [Fact]
    public async Task AbbreviatedSha_IsRejected()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);

        var answer = await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _headSha[..12]);

        answer.IsError.ShouldBe(true);
        FirstLine(answer).ShouldBe("Commit: unknown");
        answer.StructuredString("commitSha").ShouldBeNull();
        answer.Text().ShouldContain(CommitScopeResolver.InvalidShaCode);
        (await ProjectsController().FileImpact(Repo, "src/a.py", sha: _headSha[..12])).Result
            .ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ShaOfTheDefaultBranchIndex_AnswersFromThatIndex_ReadingTheCommittedBlob()
    {
        GitFixture.Write(_root, "src/a.py", "uncommitted edit\n");

        var answer = await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _baseSha);

        FirstLine(answer).ShouldBe($"Commit: {_baseSha}");
        answer.StructuredString("source").ShouldBe("default-branch");
        answer.Text().ShouldContain("base alpha");
        answer.Text().ShouldNotContain("uncommitted edit");
    }

    [Fact]
    public async Task NewerOverlayBeingBuilt_ReportsItsStatus_NotAnOlderOverlaysStaleness()
    {
        await CreateReadyOverlayAsync(_headSha, new string('e', 40));
        var pendingId = await _overlays.CreateOverlayAsync(new BranchOverlayEntity
        {
            Project = Repo,
            RequestedRef = _headSha,
            CreatedAt = Now.UtcDateTime.AddMinutes(1)
        });

        var queued = await Mcp().GetCodeSnippet(Repo, "src/a.py", sha: _headSha);
        AssertNotIndexed(queued, _headSha, BranchOverlayStatuses.Queued);
        queued.StructuredContent!.Value.GetProperty("notIndexed").GetProperty("overlayId").GetInt64().ShouldBe(pendingId);

        await _overlays.MarkOverlayIndexingAsync(pendingId, Now.UtcDateTime.AddMinutes(1));
        var indexing = await ProjectsController().FileImpact(Repo, "src/a.py", sha: _headSha);
        var body = indexing.Result.ShouldBeOfType<ConflictObjectResult>().Value.ShouldBeOfType<CommitNotIndexedResponse>();
        body.Status.ShouldBe(BranchOverlayStatuses.Indexing);
        body.OverlayId.ShouldBe(pendingId);
    }

    [Fact]
    public async Task ShaOfTheDefaultBranchIndex_WithoutProject_AnswersNodeSourceAndDetail()
    {
        // No overlay row carries the SHA; the node surfaces take no project.
        var mcp = await Mcp().ReadNodeSource(_alphaId, _baseSha);
        FirstLine(mcp).ShouldBe($"Commit: {_baseSha}");
        mcp.StructuredString("source").ShouldBe("default-branch");
        mcp.Text().ShouldContain("base alpha");

        var rest = (NodeSourceResponse)((OkObjectResult)(await NodesController().Source(_alphaId, _baseSha)).Result!).Value!;
        rest.CommitSha.ShouldBe(_baseSha);
        rest.Content.ShouldContain("base alpha");

        var detail = (NodeDetailResponse)((OkObjectResult)(await NodesController().Detail(_alphaId, _baseSha)).Result!).Value!;
        detail.CommitSha.ShouldBe(_baseSha);
    }

    // ── No SHA: the default-branch index commit, not the working tree ────

    [Fact]
    public async Task NoSha_ReadsTheIndexedCommit_WhenTheCheckoutIsAheadOfTheIndex()
    {
        // The shared checkout moved past the indexed commit (e.g. reset to the remote head) without a re-index.
        GitFixture.Write(_root, "src/a.py", "def alpha():\n    return 'ahead alpha'\n");
        GitFixture.Run(_root, "commit", "-q", "-am", "ahead");
        GitFixture.Write(_root, "src/b.py", "uncommitted edit\n");

        var snippet = await Mcp().GetCodeSnippet(Repo, "src/a.py");
        FirstLine(snippet).ShouldBe($"Commit: {_baseSha}");
        snippet.Text().ShouldContain("base alpha");
        snippet.Text().ShouldNotContain("ahead alpha");

        (await Mcp().ReadNodeSource(_betaId)).Text().ShouldNotContain("uncommitted edit");
        var rest = (NodeSourceResponse)((OkObjectResult)(await NodesController().Source(_alphaId)).Result!).Value!;
        rest.CommitSha.ShouldBe(_baseSha);
        rest.Content.ShouldContain("base alpha");
    }

    [Fact]
    public async Task NoSha_NonGitFolderSource_ReadsTheWorkingTree()
    {
        var folder = Path.Combine(_root + "-folder");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "notes.py"), "def folder_fn():\n    return 'folder'\n");
            await _store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "Folder",
                LocalPath = folder,
                LastCommitSha = new string('a', 40)
            });

            var snippet = await Mcp().GetCodeSnippet("Folder", "notes.py");

            FirstLine(snippet).ShouldBe($"Commit: {new string('a', 40)}");
            snippet.Text().ShouldContain("return 'folder'");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── AC3: every answer names its commit ────────────────────────────────

    [Fact]
    public async Task PinnedAnswer_NamesTheDefaultCommitOfEveryOtherRepository()
    {
        var otherSha = new string('f', 40);
        await _store.UpsertRepositoryAsync(new RepositoryEntity { Name = "Other", LastCommitSha = otherSha });
        var callerId = _store.AddNode(new GraphNode
        {
            Project = "Other",
            Label = NodeLabel.Function,
            Name = "remote_caller",
            QualifiedName = "Other:src/r.py:remote_caller",
            FilePath = "src/r.py",
            StartLine = 1,
            EndLine = 2
        });
        _store.AddEdge(new GraphEdge { Project = "Other", SourceId = callerId, TargetId = _alphaId, Type = EdgeType.CALLS });
        await _store.InsertCrossRepoEdgeAsync(new CrossRepoEdge
        {
            SourceProject = "Other",
            TargetProject = Repo,
            SourceNodeId = callerId,
            TargetNodeId = _alphaId,
            Type = EdgeType.CALLS
        });
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();

        foreach (var answer in new[]
                 {
                     await mcp.TraceCallPath("alpha", "inbound", project: Repo, sha: _headSha),
                     await mcp.AnalyzeImpact(Qn("src/a.py", "alpha"), Repo, sha: _headSha),
                     // alpha has no data edges: Other appears only through the cross-repo reference.
                     await mcp.TraceDataLineage("alpha", Repo, _headSha),
                 })
        {
            answer.Text().ShouldContain("Other");
            FirstLine(answer).ShouldBe($"Commit: {_headSha}");
            answer.StructuredString("commitSha").ShouldBe(_headSha);
            var commits = answer.StructuredContent!.Value.GetProperty("commits");
            commits.GetProperty(Repo).GetString().ShouldBe(_headSha);
            commits.GetProperty("Other").GetString().ShouldBe(otherSha);
        }

        var rest = (ImpactReport)((OkObjectResult)(await ProjectsController().Impact(Repo, Qn("src/a.py", "alpha"), sha: _headSha)).Result!).Value!;
        rest.CommitSha.ShouldBe(_headSha);
        rest.Commits.ShouldNotBeNull();
        rest.Commits[Repo].ShouldBe(_headSha);
        rest.Commits["Other"].ShouldBe(otherSha);
    }

    [Fact]
    public async Task NodeDetail_WithSha_ReturnsTheOverlayNode()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var headAlphaId = (await (await Resolver().ResolveAsync(Repo, _headSha)).Graph
            .FindNodeByQualifiedNameAsync(Repo, Qn("src/a.py", "alpha")))!.Id;
        var nodes = NodesController();

        var head = (NodeDetailResponse)((OkObjectResult)(await nodes.Detail(headAlphaId, _headSha)).Result!).Value!;
        head.CommitSha.ShouldBe(_headSha);
        head.Node.Id.ShouldBe(headAlphaId);
        head.Node.StartLine.ShouldBe(6);
        head.InboundEdges.Select(edge => edge.NeighborName).ShouldBe(["beta", "newbie"], ignoreOrder: true);

        (await nodes.Detail(headAlphaId)).Result.ShouldBeOfType<NotFoundResult>();
        (await nodes.Detail(_deltaId, _headSha)).Result.ShouldBeOfType<NotFoundResult>();
        (await nodes.Detail(_alphaId, new string('c', 40))).Result.ShouldBeOfType<ConflictObjectResult>();

        var defaultBranch = (NodeDetailResponse)((OkObjectResult)(await nodes.Detail(_alphaId)).Result!).Value!;
        defaultBranch.CommitSha.ShouldBe(_baseSha);
        defaultBranch.Node.StartLine.ShouldBe(1);
    }

    [Fact]
    public async Task EveryMcpAnswer_CarriesItsCommit_WithAndWithoutSha()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();
        var headAlphaId = (await (await Resolver().ResolveAsync(Repo, _headSha)).Graph
            .FindNodeByQualifiedNameAsync(Repo, Qn("src/a.py", "alpha")))!.Id;

        foreach (var (sha, expected) in new[] { ((string?)null, _baseSha), (_headSha, _headSha) })
        {
            var answers = new List<CallToolResult>
            {
                await mcp.SearchGraph("alpha", project: Repo, sha: sha),
                await mcp.SearchGraph("alpha", sha: sha),
                await mcp.TraceCallPath("alpha", project: Repo, sha: sha),
                await mcp.TraceDataLineage("alpha", Repo, sha),
                await mcp.FindConsumers("alpha", Repo, sha),
                await mcp.FindPublishers("alpha", Repo, sha),
                await mcp.AnalyzeImpact("alpha", Repo, sha: sha),
                await mcp.GetCodeSnippet(Repo, "src/a.py", sha: sha),
                await mcp.ReadNodeSource(sha is null ? _alphaId : headAlphaId, sha),
            };

            foreach (var answer in answers)
            {
                FirstLine(answer).ShouldBe($"Commit: {expected}");
                answer.StructuredString("commitSha").ShouldBe(expected);
            }
        }

        var headSource = await mcp.ReadNodeSource(headAlphaId, _headSha);
        headSource.Text().ShouldContain("head alpha");
        headSource.Text().ShouldContain("lines 6–7");
        (await mcp.SearchGraph("newbie", sha: _headSha)).Text().ShouldContain("newbie");
        (await mcp.SearchGraph("newbie", project: Repo)).Body().ShouldStartWith("No results found");
    }

    [Fact]
    public async Task ConsolidatedTools_CarryCommitInEnvelopeAndStructuredContent()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();

        var results = new[]
        {
            await ConsolidatedMcpServer.CodegraphSearch(mcp, "search", "alpha", project: Repo, sha: _headSha),
            await ConsolidatedMcpServer.GraphTrace(mcp, "call_path", "alpha", project: Repo, sha: _headSha),
            await ConsolidatedMcpServer.GraphTrace(mcp, "impact", "alpha", project: Repo, sha: _headSha),
            await ConsolidatedMcpServer.GraphSource(mcp, "snippet", Repo, "src/a.py", sha: _headSha),
        };

        foreach (var result in results)
        {
            using var envelope = JsonDocument.Parse(result.Text());
            envelope.RootElement.GetProperty("commitSha").GetString().ShouldBe(_headSha);
            envelope.RootElement.GetProperty("result").GetString()!.ShouldStartWith($"Commit: {_headSha}\n");
            result.StructuredString("commitSha").ShouldBe(_headSha);
        }

        var notIndexed = await ConsolidatedMcpServer.GraphSource(mcp, "snippet", Repo, "src/a.py", sha: new string('c', 40));
        using (var envelope = JsonDocument.Parse(notIndexed.Text()))
            envelope.RootElement.GetProperty("notIndexed").GetProperty("status").GetString().ShouldBe("unknown");

        var withoutSha = await ConsolidatedMcpServer.GraphSource(mcp, "snippet", Repo, "src/a.py");
        using (var envelope = JsonDocument.Parse(withoutSha.Text()))
            envelope.RootElement.GetProperty("commitSha").GetString().ShouldBe(_baseSha);
    }

    [Fact]
    public async Task RestReadSurfaces_CarryCommitSha_WithAndWithoutSha()
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);
        var nodes = NodesController();

        var headSource = (NodeSourceResponse)((OkObjectResult)(await nodes.Source(_alphaId, _headSha)).Result!).Value!;
        headSource.CommitSha.ShouldBe(_headSha);
        headSource.Content.ShouldContain("head alpha");
        headSource.StartLine.ShouldBe(6);
        var baseSource = (NodeSourceResponse)((OkObjectResult)(await nodes.Source(_alphaId)).Result!).Value!;
        baseSource.CommitSha.ShouldBe(_baseSha);
        baseSource.Content.ShouldContain("base alpha");

        var headSearch = (NodeListResponse)((OkObjectResult)(await nodes.Search("%", Repo, sha: _headSha)).Result!).Value!;
        headSearch.CommitSha.ShouldBe(_headSha);
        headSearch.Items.Select(node => node.Name).ShouldBe(["added_fn", "alpha", "beta", "newbie"], ignoreOrder: true);
        var baseSearch = (NodeListResponse)((OkObjectResult)(await nodes.Search("%", Repo)).Result!).Value!;
        baseSearch.CommitSha.ShouldBe(_baseSha);
        baseSearch.Items.Select(node => node.Name).ShouldBe(["alpha", "gone", "beta", "delta"], ignoreOrder: true);

        var headByFile = (NodeByFileResponse)((OkObjectResult)(await nodes.ByFile(Repo, "src/n.py", line: 1, sha: _headSha)).Result!).Value!;
        headByFile.CommitSha.ShouldBe(_headSha);
        headByFile.NodeId.ShouldBeLessThan(0);
        var baseByFile = (NodeByFileResponse)((OkObjectResult)(await nodes.ByFile(Repo, "src/a.py", line: 1)).Result!).Value!;
        baseByFile.NodeId.ShouldBe(_alphaId);
        baseByFile.CommitSha.ShouldBe(_baseSha);
    }

    [Fact]
    public async Task ListAndSearchProjects_ShowRepoUrlAndLastCommit()
    {
#pragma warning disable CS0618 // list_projects is obsolete but still served
        var list = await Mcp().ListProjects();
#pragma warning restore CS0618
        var search = await Mcp().SearchProjects("Rep");

        foreach (var markdown in new[] { list, search })
        {
            markdown.ShouldContain("  Repo: https://github.com/example/repo.git");
            markdown.ShouldContain($"  Last commit: {_baseSha}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ── Fixture helpers ──────────────────────────────────────────────────

    private async Task<long> CreateReadyOverlayAsync(string headSha, string baseSha)
    {
        var id = await _overlays.CreateOverlayAsync(new BranchOverlayEntity
        {
            Project = Repo,
            RequestedRef = "factory/feature",
            CreatedAt = Now.UtcDateTime
        });
        await _overlays.MarkOverlayIndexingAsync(id, Now.UtcDateTime);
        (await _overlays.CompleteOverlayAsync(
            id,
            headSha,
            baseSha,
            [
                new BranchOverlayFileEntity { OverlayId = id, FilePath = "src/a.py", ChangeKind = BranchOverlayChangeKinds.Modified },
                new BranchOverlayFileEntity { OverlayId = id, FilePath = "src/d.py", ChangeKind = BranchOverlayChangeKinds.Deleted },
                new BranchOverlayFileEntity { OverlayId = id, FilePath = "src/n.py", ChangeKind = BranchOverlayChangeKinds.Added }
            ],
            [
                Function("added_fn", "src/a.py", 3, 4),
                Function("alpha", "src/a.py", 6, 7),
                Function("newbie", "src/n.py", 1, 2)
            ],
            [
                new PendingEdge(Qn("src/n.py", "newbie"), Qn("src/a.py", "alpha"), EdgeType.CALLS)
            ],
            Now.UtcDateTime)).ShouldBeTrue();
        return id;
    }

    private CommitScopeResolver Resolver() => new(
        _store,
        Options.Create(new RepositorySourceOptions()),
        _overlays,
        new BranchOverlayGit(),
        Options.Create(new BranchOverlayOptions()),
        _time);

    private CodeGraphMcpServer Mcp() => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
        commitScopes: Resolver());

    private ProjectsController ProjectsController() => new(
        null!,
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
        null!,
        Resolver());

    private NodesController NodesController() => new(
        new NodeQueryService(_store, Options.Create(new RepositorySourceOptions()), Resolver()),
        Resolver());

    private static void AssertNotIndexed(CallToolResult answer, string sha, string status)
    {
        FirstLine(answer).ShouldBe($"Commit: {sha}");
        answer.StructuredString("commitSha").ShouldBe(sha);
        var notIndexed = answer.StructuredContent!.Value.GetProperty("notIndexed");
        notIndexed.GetProperty("code").GetString().ShouldBe(CommitScopeResolver.NotIndexedCode);
        notIndexed.GetProperty("status").GetString().ShouldBe(status);
        notIndexed.GetProperty("sha").GetString().ShouldBe(sha);
        answer.Text().ShouldContain($"status: {status}");
    }

    private static string FirstLine(CallToolResult result) => result.Text().Split('\n')[0];

    private static string Qn(string path, string name) => $"{Repo}:{path}:{name}";

    private static GraphNode Function(string name, string path, int start, int end) => new()
    {
        Project = Repo,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = Qn(path, name),
        FilePath = path,
        StartLine = start,
        EndLine = end
    };

    private static GraphEdge Calls(long source, long target) => new()
    {
        Project = Repo,
        SourceId = source,
        TargetId = target,
        Type = EdgeType.CALLS
    };
}
