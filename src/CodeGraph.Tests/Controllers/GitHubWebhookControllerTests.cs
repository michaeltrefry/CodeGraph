using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Services;
using CodeGraph.Services.Indexer;
using CodeGraph.Tests.Extractors;
using CodeGraph.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class GitHubWebhookControllerTests
{
    private const string Secret = "webhook-test-secret";
    private const string HeadSha = "4f2c9a1b7e3d5c8f0a6b2d4e1f3a5c7b9d0e2f4a";

    private readonly StandaloneIndexerOperationsServiceTests.FakeIndexerRunStore _runs = new();
    private readonly StandaloneIndexerOperationsServiceTests.RecordingBackgroundRunner _runner = new();
    private readonly InMemoryGraphStore _graph = new();

    public GitHubWebhookControllerTests()
    {
        _graph.AddRepository(new RepositoryInfo(
            "dark-factory-sandbox", "https://github.com/michaeltrefry/dark-factory-sandbox.git",
            "michaeltrefry", null, "252dc0d8", null, null, null, false, null));
    }

    [Fact]
    public async Task SignedPushToDefaultBranch_QueuesIncrementalSyncForThatRepository()
    {
        var result = await DeliverAsync("push", PushBody());

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        accepted.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
        var run = (await RunsAsync()).ShouldHaveSingleItem();
        run.Operation.ShouldBe(IndexerRunOperations.ProcessRepositories);
        run.RequestedByUsername.ShouldBe(GitHubPushWebhookService.RequestedBy);
        run.SubmissionKey.ShouldBe($"github-push:dark-factory-sandbox:{HeadSha}");
        var request = JsonSerializer.Deserialize<ProcessRequest>(run.ArgsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        request.Repos.ShouldBe(["dark-factory-sandbox"]);
        request.ShouldIndex.ShouldBeTrue();
        request.ShouldAnalyze.ShouldBeTrue();
        request.SkipIfUpToDate.ShouldBeTrue();
        _runner.EnqueuedRunIds.ShouldBe([run.Id]);
    }

    [Fact]
    public async Task PushMatchedByFullNameWhenStoredUrlDiffers_QueuesSync()
    {
        var graph = new InMemoryGraphStore();
        graph.AddRepository(new RepositoryInfo("dark-factory-sandbox", null, "MichaelTrefry", null, null, null, null, null, false, null));

        var result = await DeliverAsync("push", PushBody(), graph: graph);

        result.ShouldBeOfType<AcceptedResult>();
        (await RunsAsync()).ShouldHaveSingleItem().Target.ShouldBe("dark-factory-sandbox");
    }

    [Fact]
    public async Task DuplicateDeliveryForSameHeadSha_QueuesAtMostOneSync()
    {
        await DeliverAsync("push", PushBody());
        var second = await DeliverAsync("push", PushBody());

        second.ShouldBeOfType<AcceptedResult>();
        (await RunsAsync()).ShouldHaveSingleItem();
        _runner.EnqueuedRunIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NewHeadSha_QueuesAnotherSync()
    {
        await DeliverAsync("push", PushBody());
        await DeliverAsync("push", PushBody(after: "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d"));

        (await RunsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task PushToOtherBranch_IsAcknowledgedAndIgnored()
    {
        var result = await DeliverAsync("push", PushBody(gitRef: "refs/heads/feature/sc-1"));

        result.ShouldBeOfType<OkObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task PushToUnknownRepository_IsAcknowledgedAndIgnored()
    {
        var result = await DeliverAsync("push", PushBody(fullName: "someone-else/dark-factory-sandbox"));

        result.ShouldBeOfType<OkObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task NonPushEvent_IsAcknowledgedAndIgnored()
    {
        var result = await DeliverAsync("pull_request", PushBody());

        result.ShouldBeOfType<OkObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DefaultBranchDeletion_IsAcknowledgedAndIgnored()
    {
        var result = await DeliverAsync("push", PushBody(after: new string('0', 40), deleted: true));

        result.ShouldBeOfType<OkObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RepositoryExcludedByGroupRule_IsAcknowledgedAndIgnored()
    {
        await AddExclusionAsync("group", "michaeltrefry", "complete");

        var result = await DeliverAsync("push", PushBody());

        result.ShouldBeOfType<OkObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RepositoryExcludedFromAnalysisByGroupRule_QueuesIndexOnlySync()
    {
        await AddExclusionAsync("group", "michaeltrefry", "no_analysis");

        var result = await DeliverAsync("push", PushBody());

        result.ShouldBeOfType<AcceptedResult>();
        var run = (await RunsAsync()).ShouldHaveSingleItem();
        var request = JsonSerializer.Deserialize<ProcessRequest>(run.ArgsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        request.ShouldIndex.ShouldBeTrue();
        request.ShouldAnalyze.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoteIndexerSubmissionConflict_IsAcknowledgedAndIgnored()
    {
        _runs.CreateFailure = new IndexerClientException(HttpStatusCode.Conflict, "idempotency_conflict", "conflict");

        var result = await DeliverAsync("push", PushBody());

        result.ShouldBeOfType<OkObjectResult>();
        _runner.EnqueuedRunIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingSignature_IsRefusedAndQueuesNothing()
    {
        var result = await DeliverAsync("push", PushBody(), signature: null);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sha256=00")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha1=0123456789abcdef")]
    public async Task InvalidSignature_IsRefusedAndQueuesNothing(string signature)
    {
        var result = await DeliverAsync("push", PushBody(), signature: signature);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SignatureFromAnotherSecret_IsRefusedAndQueuesNothing()
    {
        var body = PushBody();
        var result = await DeliverAsync("push", body, signature: Sign("another-secret", body));

        result.ShouldBeOfType<UnauthorizedObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task BodyTamperedAfterSigning_IsRefused()
    {
        var signature = Sign(Secret, PushBody());
        var result = await DeliverAsync("push", PushBody(after: "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d"), signature: signature);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task NoConfiguredSecret_RefusesEveryDelivery()
    {
        var body = PushBody();
        var result = await DeliverAsync("push", body, signature: Sign("", body), secret: "");

        result.ShouldBeOfType<UnauthorizedObjectResult>();
        (await RunsAsync()).ShouldBeEmpty();
    }

    private async Task<IActionResult> DeliverAsync(
        string eventName,
        string body,
        string? signature = "",
        string secret = Secret,
        InMemoryGraphStore? graph = null)
    {
        var options = new RepositorySourceOptions();
        options.GitHub.WebhookSecret = secret;
        graph ??= _graph;
        var service = new GitHubPushWebhookService(
            Options.Create(options),
            graph,
            new ExclusionService(graph, NullLogger<ExclusionService>.Instance),
            new StandaloneIndexerOperationsService(_runs, new StandaloneIndexerOperationsServiceTests.FakeDatabaseSourceStore(), _runner),
            NullLogger<GitHubPushWebhookService>.Instance);

        var bytes = Encoding.UTF8.GetBytes(body);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.Headers["X-GitHub-Event"] = eventName;
        signature = signature == "" ? Sign(secret, body) : signature;
        if (signature is not null)
            httpContext.Request.Headers["X-Hub-Signature-256"] = signature;

        var controller = new GitHubWebhookController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return await controller.Receive(CancellationToken.None);
    }

    private Task AddExclusionAsync(string targetType, string targetValue, string exclusionType)
        => _graph.CreateExclusionRuleAsync(new ExclusionRuleEntity
        {
            TargetType = targetType,
            TargetValue = targetValue,
            ExclusionType = exclusionType,
            CreatedBy = "test"
        });

    private Task<IReadOnlyList<IndexerRunEntity>> RunsAsync() => _runs.ListIndexerRunsAsync(take: 200);

    private static string Sign(string secret, string body)
        => "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string PushBody(
        string gitRef = "refs/heads/main",
        string after = HeadSha,
        bool deleted = false,
        string fullName = "michaeltrefry/dark-factory-sandbox")
        => JsonSerializer.Serialize(new
        {
            @ref = gitRef,
            before = "252dc0d8a1b2c3d4e5f60718293a4b5c6d7e8f90",
            after,
            deleted,
            repository = new
            {
                name = fullName.Split('/')[1],
                full_name = fullName,
                default_branch = "main",
                clone_url = $"https://github.com/{fullName}.git",
                html_url = $"https://github.com/{fullName}"
            }
        });
}
