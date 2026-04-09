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
        => new(service, NullLogger<RepositoryReviewsController>.Instance);

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

        public Task<long> StartReviewAsync(string repo, string mode, CancellationToken ct = default)
            => StartReviewAsyncHandler is null
                ? Task.FromResult(1L)
                : StartReviewAsyncHandler(repo, mode, ct);

        public Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RepositoryReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default)
            => Task.FromResult(ReviewResponses.Count > 0 ? ReviewResponses.Dequeue() : LatestResponse);

        public Task<RepositoryReviewResponse?> GetLatestReviewAsync(string repo, CancellationToken ct = default)
            => Task.FromResult(LatestResponse);
    }
}
