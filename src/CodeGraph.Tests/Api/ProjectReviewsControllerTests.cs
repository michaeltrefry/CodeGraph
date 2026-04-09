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

public class ProjectReviewsControllerTests
{
    [Fact]
    public async Task Start_ReturnsAcceptedQueuedResponse()
    {
        var service = new FakeProjectReviewService
        {
            StartReviewAsyncHandler = (_, _, _, _) => Task.FromResult(42L)
        };
        var controller = CreateController(service);

        var result = await controller.Start("TestRepo", new StartProjectReviewRequest("TestRepo.Api"), CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        var payload = accepted.Value.ShouldBeOfType<StartProjectReviewResponse>();
        payload.ReviewRunId.ShouldBe(42);
        payload.Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Latest_ReturnsNotFoundWhenMissing()
    {
        var controller = CreateController(new FakeProjectReviewService());

        var result = await controller.Latest("TestRepo", "TestRepo.Api", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Diagnostics_ReturnsServicePayload()
    {
        var service = new FakeProjectReviewService
        {
            DiagnosticsResponse = new ProjectDiagnosticsResponse(
                "TestRepo",
                "TestRepo.Api",
                1,
                2,
                3,
                [
                    new ProjectDiagnosticResponse(
                        "roslyn",
                        "CS8602",
                        "warning",
                        "Possible null dereference",
                        "nullable",
                        "src/FooService.cs",
                        12,
                        12,
                        DateTime.UtcNow)
                ])
        };
        var controller = CreateController(service);

        var result = await controller.Diagnostics("TestRepo", "TestRepo.Api", CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<ProjectDiagnosticsResponse>();
        payload.WarningCount.ShouldBe(2);
        payload.Diagnostics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stream_WritesStatusFindingAndCompletedEvents()
    {
        var running = CreateReview("running");
        var completed = CreateReview(
            "completed",
            findings:
            [
                new ProjectReviewFindingResponse(
                    "high",
                    "bug",
                    "Null path",
                    "The code dereferences a nullable result.",
                    "Input is used without a null guard.",
                    "src/FooService.cs",
                    10,
                    10,
                    "Guard the null case before dereferencing.",
                    "high")
            ]);

        var service = new FakeProjectReviewService();
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

    private static ProjectReviewsController CreateController(FakeProjectReviewService service)
        => new(service, NullLogger<ProjectReviewsController>.Instance);

    private static ProjectReviewResponse CreateReview(
        string status,
        IReadOnlyList<ProjectReviewFindingResponse>? findings = null)
        => new(
            new ProjectReviewRunResponse(
                42,
                "TestRepo",
                "TestRepo.Api",
                "abc123",
                status,
                "standard",
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
            []);

    private sealed class FakeProjectReviewService : IProjectReviewService
    {
        public Func<string, string, string, CancellationToken, Task<long>>? StartReviewAsyncHandler { get; init; }
        public ProjectDiagnosticsResponse DiagnosticsResponse { get; init; } =
            new("TestRepo", null, 0, 0, 0, []);
        public Queue<ProjectReviewResponse?> ReviewResponses { get; } = new();
        public ProjectReviewResponse? LatestResponse { get; init; }

        public Task<long> StartReviewAsync(string repo, string projectName, string mode, CancellationToken ct = default)
            => StartReviewAsyncHandler is null
                ? Task.FromResult(1L)
                : StartReviewAsyncHandler(repo, projectName, mode, ct);

        public Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ProjectReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default)
            => Task.FromResult(ReviewResponses.Count > 0 ? ReviewResponses.Dequeue() : LatestResponse);

        public Task<ProjectReviewResponse?> GetLatestReviewAsync(string repo, string projectName, CancellationToken ct = default)
            => Task.FromResult(LatestResponse);

        public Task<ProjectDiagnosticsResponse> GetDiagnosticsAsync(string repo, string? dotnetProject = null, CancellationToken ct = default)
            => Task.FromResult(DiagnosticsResponse);
    }
}
