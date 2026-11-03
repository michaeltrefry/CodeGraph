using System.Text;
using CodeGraph.Api.Controllers;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Reviews;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Api;

public class RepositoryReviewsControllerTests
{
    [Fact]
    public async Task Start_ReturnsAcceptedQueuedResponse()
    {
        var service = new FakeRepositoryReviewService
        {
            StartReviewAsyncHandler = (_, _, _) => Task.FromResult(42L)
        };
        var controller = CreateController(service);

        var result = await controller.Start("TestRepo", new StartRepositoryReviewRequest("full"), CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        var payload = accepted.Value.ShouldBeOfType<StartRepositoryReviewResponse>();
        payload.ReviewRunId.ShouldBe(42);
        payload.Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Start_RecordsRequestingUser()
    {
        var service = new FakeRepositoryReviewService();
        var controller = CreateController(service);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("preferred_username", "michael")], "test"))
            }
        };

        await controller.Start("TestRepo", new StartRepositoryReviewRequest("full"), CancellationToken.None);

        service.StartedBy.ShouldBe("michael");
    }

    [Fact]
    public async Task List_ReturnsNotFoundWhenRepositoryUnknown()
    {
        var controller = CreateController(new FakeRepositoryReviewService());

        var result = await controller.List("Nope");

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(500, 100)]
    public async Task List_ClampsLimitAndPassesCursor(int requested, int expected)
    {
        var service = new FakeRepositoryReviewService { ListResponse = new RepositoryReviewRunPage([], null) };
        var controller = CreateController(service);

        var result = await controller.List("TestRepo", requested, 77);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBe(service.ListResponse);
        service.ListCall.ShouldBe(("TestRepo", expected, 77L));
    }

    [Fact]
    public async Task Latest_ReturnsNotFoundWhenMissing()
    {
        var controller = CreateController(new FakeRepositoryReviewService());

        var result = await controller.Latest("TestRepo", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Stream_WritesStatusFindingAndCompletedEvents()
    {
        var running = CreateReview("running");
        var completed = CreateReview(
            "completed",
            findings:
            [
                new RepositoryReviewFindingResponse(
                    "high",
                    "bug",
                    "Null path",
                    "The code dereferences a nullable result.",
                    "Input is used without a null guard.",
                    "src/FooService.cs",
                    10,
                    10,
                    "Guard the null case before dereferencing.",
                    "high",
                    "TestRepo.Api")
            ]);

        var service = new FakeRepositoryReviewService();
        service.ReviewResponses.Enqueue(running);
        service.ReviewResponses.Enqueue(completed);

        var controller = CreateController(service);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.Stream("TestRepo", 42, CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        var output = Encoding.UTF8.GetString(((MemoryStream)httpContext.Response.Body).ToArray());
        output.ShouldContain("\"type\":\"status\"");
        output.ShouldContain("\"type\":\"finding\"");
        output.ShouldContain("\"type\":\"completed\"");
    }

    private static RepositoryReviewsController CreateController(FakeRepositoryReviewService service)
        => new(service, NullLogger<RepositoryReviewsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static RepositoryReviewResponse CreateReview(
        string status,
        IReadOnlyList<RepositoryReviewFindingResponse>? findings = null)
        => new(
            new RepositoryReviewRunResponse(
                42,
                "TestRepo",
                "abc123",
                null,
                null,
                status,
                "full",
                "v1",
                "test-model",
                DateTime.UtcNow.AddMinutes(-2),
                DateTime.UtcNow.AddMinutes(-1),
                status == "completed" ? DateTime.UtcNow : null,
                status == "failed" ? "Boom" : null),
            "Overview",
            findings ?? [],
            [],
            [],
            [],
            [],
            []);

    private sealed class FakeRepositoryReviewService : IRepositoryReviewService
    {
        public Func<string, string, CancellationToken, Task<long>>? StartReviewAsyncHandler { get; init; }
        public Queue<RepositoryReviewResponse?> ReviewResponses { get; } = new();
        public RepositoryReviewResponse? LatestResponse { get; init; }
        public RepositoryReviewRunPage? ListResponse { get; init; }
        public (string Repo, int Limit, long? Before)? ListCall { get; private set; }
        public string? StartedBy { get; private set; }

        public Task<long> StartReviewAsync(string repo, string mode, string? requestedBy = null, CancellationToken ct = default)
        {
            StartedBy = requestedBy;
            return StartReviewAsyncHandler is null
                ? Task.FromResult(1L)
                : StartReviewAsyncHandler(repo, mode, ct);
        }

        public Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RepositoryReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default)
            => Task.FromResult(ReviewResponses.Count > 0 ? ReviewResponses.Dequeue() : LatestResponse);

        public Task<RepositoryReviewResponse?> GetLatestReviewAsync(string repo, CancellationToken ct = default)
            => Task.FromResult(LatestResponse);

        public Task<RepositoryReviewRunPage?> ListReviewsAsync(string repo, int limit, long? before = null, CancellationToken ct = default)
        {
            ListCall = (repo, limit, before);
            return Task.FromResult(ListResponse);
        }
    }
}
