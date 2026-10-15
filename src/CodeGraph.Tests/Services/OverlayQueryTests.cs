using System.Text.Json;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
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

    // ── Per-file risk tools: analyze_impact_file and file_hotspot ─────────

    [Fact]
    public async Task AnalyzeImpactFile_DefaultBranch_AnswersStructuredBlastRadiusAtTheIndexCommit()
    {
        // Affected nodes at low, critical and medium risk; the changed node (always "critical") does not count.
        AffectedNode Affected(string name, RiskLevel risk, bool lowConfidence = false) =>
            new(1, name, name, "Function", Repo, null, "src/b.py", 1, "CALLS", risk, [], lowConfidence);
        var stub = new StubImpactAnalysis(new ImpactReport(
            [new AffectedNode(_alphaId, "alpha", "alpha", "Function", Repo, null, "src/a.py", 0, "CHANGED", RiskLevel.Critical, [])],
            [Affected("x", RiskLevel.Low), Affected("y", RiskLevel.Critical, lowConfidence: true), Affected("z", RiskLevel.Medium)],
            [],
            new ImpactSummary(TotalAffected: 3, CrossRepoCount: 1, CriticalCount: 1, HighCount: 0, MediumCount: 1, LowCount: 1,
                AffectedProjects: [Repo], LowConfidenceCount: 1)));

        var answer = await Mcp(stub).AnalyzeImpactFile(Repo, "./src/a.py");

        stub.Calls.ShouldBe([(Repo, "src/a.py", 3)]);
        answer.IsError.ShouldNotBe(true);
        FirstLine(answer).ShouldBe($"Commit: {_baseSha}");
        var s = answer.StructuredContent!.Value;
        s.GetProperty("commitSha").GetString().ShouldBe(_baseSha);
        s.GetProperty("source").GetString().ShouldBe("default-branch");
        s.GetProperty("path").GetString().ShouldBe("src/a.py");
        s.GetProperty("found").GetBoolean().ShouldBeTrue();
        s.GetProperty("blastRadius").GetString().ShouldBe("critical");
        s.GetProperty("totalAffected").GetInt32().ShouldBe(3);
        s.GetProperty("criticalCount").GetInt32().ShouldBe(1);
        s.GetProperty("highCount").GetInt32().ShouldBe(0);
        s.GetProperty("mediumCount").GetInt32().ShouldBe(1);
        s.GetProperty("lowCount").GetInt32().ShouldBe(1);
        s.GetProperty("crossRepoCount").GetInt32().ShouldBe(1);
        s.GetProperty("lowConfidenceCount").GetInt32().ShouldBe(1);
        s.GetProperty("unmappedInboundEdges").ValueKind.ShouldBe(JsonValueKind.Null);

        // depth is clamped to 1..5 like analyze_impact.
        await Mcp(stub).AnalyzeImpactFile(Repo, "src/a.py", depth: 99);
        stub.Calls[^1].Depth.ShouldBe(5);
    }

    [Theory]
    [InlineData(new[] { RiskLevel.Low }, "low")]
    [InlineData(new[] { RiskLevel.Low, RiskLevel.Medium }, "medium")]
    [InlineData(new[] { RiskLevel.Medium, RiskLevel.High, RiskLevel.Low }, "high")]
    [InlineData(new RiskLevel[0], "none")]
    public void BlastRadius_IsTheWorstRiskAmongAffectedNodes(RiskLevel[] risks, string expected)
    {
        var report = new ImpactReport(
            [new AffectedNode(1, "c", "c", "Function", Repo, null, "a", 0, "CHANGED", RiskLevel.Critical, [])],
            risks.Select((risk, i) => new AffectedNode(i + 2, "n", "n", "Function", Repo, null, "b", 1, "CALLS", risk, [])).ToList(),
            [],
            new ImpactSummary(risks.Length, 0, 0, 0, 0, 0, []));

        CodeGraphMcpServer.BlastRadius(report).ShouldBe(expected);
        CodeGraphMcpServer.BlastRadius(null).ShouldBe("none");
    }

    [Fact]
    public async Task AnalyzeImpactFile_FileWithNodesButNoDependents_IsFoundWithNoBlastRadius()
    {
        var s = (await Mcp().AnalyzeImpactFile(Repo, "src/b.py")).StructuredContent!.Value;

        s.GetProperty("found").GetBoolean().ShouldBeTrue();
        s.GetProperty("blastRadius").GetString().ShouldBe("none");
        s.GetProperty("totalAffected").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task AnalyzeImpactFile_FileWithNoNodes_AnswersFoundFalseAndNone()
    {
        var answer = await Mcp().AnalyzeImpactFile(Repo, "src/missing.py");

        answer.IsError.ShouldNotBe(true);
        answer.StructuredString("commitSha").ShouldBe(_baseSha);
        var s = answer.StructuredContent!.Value;
        s.GetProperty("found").GetBoolean().ShouldBeFalse();
        s.GetProperty("blastRadius").GetString().ShouldBe("none");
        s.GetProperty("totalAffected").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PerFileTools_MatchThePathCaseSensitively()
    {
        await _store.UpsertFileMetricsBatchAsync(Repo,
            [new FileMetricsEntity { Project = Repo, FilePath = "src/a.py", RiskScore = 5, ComputedAt = DateTime.UtcNow, CommitSha = _baseSha }]);
        var mcp = Mcp();

        // The store's file-node lookup is case-insensitive (as MariaDB's collation is); the tools are not.
        var impact = (await mcp.AnalyzeImpactFile(Repo, "src/A.py")).StructuredContent!.Value;
        impact.GetProperty("found").GetBoolean().ShouldBeFalse();
        impact.GetProperty("blastRadius").GetString().ShouldBe("none");
        impact.GetProperty("totalAffected").GetInt32().ShouldBe(0);
        (await mcp.FileHotspot(Repo, "src/A.py")).StructuredContent!.Value
            .GetProperty("found").GetBoolean().ShouldBeFalse();

        (await mcp.AnalyzeImpactFile(Repo, "src/a.py")).StructuredContent!.Value
            .GetProperty("found").GetBoolean().ShouldBeTrue();
        (await mcp.FileHotspot(Repo, "src/a.py")).StructuredContent!.Value
            .GetProperty("found").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AnalyzeImpactFile_WithOverlaySha_AnswersFromTheOverlay_AndCountsUnmappedEdges()
    {
        var overlayId = await CreateReadyOverlayAsync(_headSha, _baseSha);
        var mcp = Mcp();

        var head = await mcp.AnalyzeImpactFile(Repo, "src/a.py", sha: _headSha);
        FirstLine(head).ShouldBe($"Commit: {_headSha}");
        var s = head.StructuredContent!.Value;
        s.GetProperty("commitSha").GetString().ShouldBe(_headSha);
        s.GetProperty("source").GetString().ShouldBe("overlay");
        s.GetProperty("overlayId").GetInt64().ShouldBe(overlayId);
        s.GetProperty("found").GetBoolean().ShouldBeTrue();
        // Head alpha is called by beta (retargeted) and newbie (added on the branch).
        s.GetProperty("totalAffected").GetInt32().ShouldBe(2);
        s.GetProperty("blastRadius").GetString().ShouldBe("high");
        // Same rule as analyze_impact: beta -> gone (into this file; gone was removed at the head) and
        // beta -> delta (from an answer node; delta.py was deleted) are left out of the answer.
        s.GetProperty("unmappedInboundEdges").GetInt32().ShouldBe(2);

        // A file deleted at the head has no nodes there, but the edge into it is still reported.
        var deleted = (await mcp.AnalyzeImpactFile(Repo, "src/d.py", sha: _headSha)).StructuredContent!.Value;
        deleted.GetProperty("found").GetBoolean().ShouldBeFalse();
        deleted.GetProperty("blastRadius").GetString().ShouldBe("none");
        deleted.GetProperty("commitSha").GetString().ShouldBe(_headSha);
        deleted.GetProperty("unmappedInboundEdges").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task AnalyzeImpactFile_WithShaWithoutReadyOverlay_ReturnsTypedNotIndexed()
    {
        var unknown = new string('c', 40);

        var answer = await Mcp().AnalyzeImpactFile(Repo, "src/a.py", sha: unknown);

        AssertNotIndexed(answer, unknown, "unknown");
        answer.StructuredContent!.Value.TryGetProperty("blastRadius", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task PerFileTools_UnknownRepoAndUnsafePath_AnswerTypedErrors()
    {
        var mcp = Mcp();

        foreach (var answer in new[]
                 {
                     await mcp.AnalyzeImpactFile("NoSuchRepo", "src/a.py"),
                     await mcp.FileHotspot("NoSuchRepo", "src/a.py")
                 })
        {
            answer.IsError.ShouldBe(true);
            answer.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()
                .ShouldBe(CodeGraphMcpServer.RepoNotFoundCode);
        }

        foreach (var path in new[] { "../secrets.txt", "/etc/passwd", "src/../../x.py", "" })
        {
            foreach (var answer in new[] { await mcp.AnalyzeImpactFile(Repo, path), await mcp.FileHotspot(Repo, path) })
            {
                answer.IsError.ShouldBe(true);
                answer.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()
                    .ShouldBe(CodeGraphMcpServer.InvalidPathCode);
            }
        }
    }

    [Fact]
    public async Task FileHotspot_ReturnsDefaultBranchMetrics_NamingTheCommitTheyWereComputedAt()
    {
        var computedAt = new DateTime(2026, 10, 9, 8, 30, 0, DateTimeKind.Utc);
        await _store.UpsertFileMetricsBatchAsync(Repo,
        [
            new FileMetricsEntity
            {
                Project = Repo, FilePath = "src/a.py", RiskScore = 42.5, ConcernScore = 7.25, HealthScore = 3.5,
                Churn365d = 12.75, ComplexityScore = 61, ComputedAt = computedAt, CommitSha = _baseSha
            },
            // Computed before the commit was recorded.
            new FileMetricsEntity
            {
                Project = Repo, FilePath = "src/b.py", RiskScore = 1, HealthScore = 9, ComputedAt = computedAt
            }
        ]);
        var mcp = Mcp();

        var found = await mcp.FileHotspot(Repo, "src/a.py");
        found.IsError.ShouldNotBe(true);
        FirstLine(found).ShouldBe($"Commit: {_baseSha}");
        var s = found.StructuredContent!.Value;
        s.GetProperty("source").GetString().ShouldBe("default-branch");
        s.GetProperty("path").GetString().ShouldBe("src/a.py");
        s.GetProperty("found").GetBoolean().ShouldBeTrue();
        s.GetProperty("riskScore").GetDouble().ShouldBe(42.5);
        s.GetProperty("concernScore").GetDouble().ShouldBe(7.25);
        s.GetProperty("healthScore").GetDouble().ShouldBe(3.5);
        s.GetProperty("churn365d").GetDouble().ShouldBe(12.75);
        s.GetProperty("complexityScore").GetInt32().ShouldBe(61);
        s.GetProperty("computedAt").GetDateTime().ShouldBe(computedAt);
        s.GetProperty("commitSha").GetString().ShouldBe(_baseSha);
        s.GetProperty("indexCommitSha").GetString().ShouldBe(_baseSha);

        var legacy = await mcp.FileHotspot(Repo, "src/b.py");
        FirstLine(legacy).ShouldBe("Commit: unknown");
        var l = legacy.StructuredContent!.Value;
        l.GetProperty("found").GetBoolean().ShouldBeTrue();
        l.GetProperty("commitSha").ValueKind.ShouldBe(JsonValueKind.Null);
        l.GetProperty("concernScore").ValueKind.ShouldBe(JsonValueKind.Null);
        l.GetProperty("indexCommitSha").GetString().ShouldBe(_baseSha);

        var missing = (await mcp.FileHotspot(Repo, "src/none.py")).StructuredContent!.Value;
        missing.GetProperty("found").GetBoolean().ShouldBeFalse();
        missing.GetProperty("riskScore").ValueKind.ShouldBe(JsonValueKind.Null);
        missing.GetProperty("commitSha").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── languageIndexed / language on file answers (sc-26137) ─────────

    /// <summary>
    /// The four kinds of file the contract tells apart: an indexed file with no data (new), an
    /// indexed file with data, a source file of an unindexed language, and a non-code file.
    /// </summary>
    public static TheoryData<string, bool, bool, string?> DefaultBranchFiles => new()
    {
        { "src/new.py", false, true, "Python" },
        { "Sources/App/New.swift", false, true, "Swift" },
        { "src/a.py", true, true, "Python" },
        { "Sources/Legacy/Bridge.m", false, false, "Objective-C" },
        { "app/src/main/Main.kt", false, false, "Kotlin" },
        { "docs/README.md", false, true, null }
    };

    [Theory]
    [MemberData(nameof(DefaultBranchFiles))]
    public async Task PerFileTools_DefaultBranch_SayWhetherTheFilesLanguageIsIndexed(
        string path, bool found, bool languageIndexed, string? language)
    {
        await _store.UpsertFileMetricsBatchAsync(Repo,
            [new FileMetricsEntity { Project = Repo, FilePath = "src/a.py", RiskScore = 5, ComputedAt = DateTime.UtcNow, CommitSha = _baseSha }]);
        var mcp = Mcp(languages: IndexedLanguageCatalogTests.ApiCatalog());

        foreach (var answer in new[] { await mcp.AnalyzeImpactFile(Repo, path), await mcp.FileHotspot(Repo, path) })
        {
            answer.IsError.ShouldNotBe(true);
            var s = answer.StructuredContent!.Value;
            s.GetProperty("found").GetBoolean().ShouldBe(found);
            AssertLanguage(s, languageIndexed, language);
            if (languageIndexed)
                answer.Text().ShouldNotContain("does not index");
            else
                answer.Text().ShouldContain($"Language: {language}, which CodeGraph does not index");
        }
    }

    public static TheoryData<string, bool, bool, string?> OverlayFiles => new()
    {
        // Added on the branch, and deleted at the head (no nodes there).
        { "src/n.py", true, true, "Python" },
        { "src/d.py", false, true, "Python" },
        { "Sources/App/New.swift", false, true, "Swift" },
        { "Sources/Legacy/Bridge.mm", false, false, "Objective-C++" },
        { "lib/main.dart", false, false, "Dart" },
        { "Package.resolved", false, true, null }
    };

    [Theory]
    [MemberData(nameof(OverlayFiles))]
    public async Task AnalyzeImpactFile_WithOverlaySha_SaysWhetherTheFilesLanguageIsIndexed(
        string path, bool found, bool languageIndexed, string? language)
    {
        await CreateReadyOverlayAsync(_headSha, _baseSha);

        var answer = await Mcp(languages: IndexedLanguageCatalogTests.ApiCatalog()).AnalyzeImpactFile(Repo, path, sha: _headSha);

        answer.IsError.ShouldNotBe(true);
        var s = answer.StructuredContent!.Value;
        s.GetProperty("source").GetString().ShouldBe("overlay");
        s.GetProperty("found").GetBoolean().ShouldBe(found);
        AssertLanguage(s, languageIndexed, language);
    }

    [Fact]
    public async Task AnalyzeImpactFile_WithShaWithoutReadyOverlay_StillSaysWhetherTheLanguageIsIndexed()
    {
        var mcp = Mcp(languages: IndexedLanguageCatalogTests.ApiCatalog());

        var unindexed = await mcp.AnalyzeImpactFile(Repo, "Sources/Bridge.m", sha: new string('c', 40));
        var indexed = await mcp.AnalyzeImpactFile(Repo, "Sources/New.swift", sha: new string('c', 40));

        AssertNotIndexed(unindexed, new string('c', 40), "unknown");
        AssertLanguage(unindexed.StructuredContent!.Value, false, "Objective-C");
        AssertLanguage(indexed.StructuredContent!.Value, true, "Swift");
    }

    [Fact]
    public async Task PerFileTools_WithoutARegisteredCatalog_FailClosed()
    {
        var mcp = Mcp();

        AssertLanguage((await mcp.AnalyzeImpactFile(Repo, "src/a.py")).StructuredContent!.Value, false, "Python");
        AssertLanguage((await mcp.FileHotspot(Repo, "docs/README.md")).StructuredContent!.Value, true, null);
    }

    private static void AssertLanguage(JsonElement structured, bool languageIndexed, string? language)
    {
        structured.GetProperty("languageIndexed").GetBoolean().ShouldBe(languageIndexed);
        var name = structured.GetProperty("language");
        if (language is null)
            name.ValueKind.ShouldBe(JsonValueKind.Null);
        else
            name.GetString().ShouldBe(language);
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

    private CodeGraphMcpServer Mcp(IImpactAnalysisService? impact = null, IndexedLanguageCatalog? languages = null) => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        impact ?? new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance),
        commitScopes: Resolver(),
        indexedLanguages: languages);

    private sealed class StubImpactAnalysis(ImpactReport report) : IImpactAnalysisService
    {
        public List<(string Project, string Path, int Depth)> Calls { get; } = [];

        public Task<ImpactReport?> AnalyzeImpactAsync(string qualifiedName, string? project = null, int maxDepth = 3, IGraphReader? graph = null)
            => throw new NotSupportedException();

        public Task<ImpactReport?> AnalyzeFileImpactAsync(string project, string filePath, int maxDepth = 3, IGraphReader? graph = null)
        {
            Calls.Add((project, filePath, maxDepth));
            return Task.FromResult<ImpactReport?>(report);
        }
    }

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
