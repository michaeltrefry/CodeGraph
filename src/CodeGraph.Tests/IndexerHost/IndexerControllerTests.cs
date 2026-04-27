using System.Security.Claims;
using CodeGraph.Indexer.Host.Controllers;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.IndexerHost;

public class IndexerControllerTests
{
    [Fact]
    public async Task ProcessRepositories_RejectsEmptyRepoList()
    {
        var controller = CreateController(new RecordingIndexerOperations());

        var result = await controller.ProcessRepositories(new ProcessRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessRepositories_RejectsOversizedRepoList()
    {
        var controller = CreateController(new RecordingIndexerOperations());
        var request = new ProcessRequest
        {
            Repos = Enumerable.Range(1, 501).Select(i => $"Repo{i}").ToList()
        };

        var result = await controller.ProcessRepositories(request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessRepositories_DelegatesToIndexerOperationsWithUsername()
    {
        var operations = new RecordingIndexerOperations();
        var controller = CreateController(operations);
        var request = new ProcessRequest { Repos = ["CodeGraph"], ShouldIndex = true };

        var result = await controller.ProcessRepositories(request, CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        accepted.Location.ShouldBe("/api/indexer/runs/77");
        accepted.Value.ShouldBeOfType<IndexerAcceptedResponse>().RunId.ShouldBe(77);
        operations.LastOperation.ShouldBe("process");
        operations.LastUsername.ShouldBe("Michael");
        operations.LastProcessRequest.ShouldBe(request);
    }

    [Fact]
    public async Task SyncSchema_RejectsNonPositiveSourceId()
    {
        var controller = CreateController(new RecordingIndexerOperations());

        var result = await controller.SyncSchema(0, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListRuns_ClampsDefaultTakeAndForwardsFilters()
    {
        var operations = new RecordingIndexerOperations
        {
            Runs =
            [
                new IndexerRunResponse(1, IndexerRunOperations.Link, "queued", "michael", "all", null, null, DateTime.UtcNow, null, null)
            ]
        };
        var controller = CreateController(operations);

        var result = await controller.ListRuns(" queued ", " link ", take: 0, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(operations.Runs);
        operations.LastStatusFilter.ShouldBe(" queued ");
        operations.LastOperationFilter.ShouldBe(" link ");
        operations.LastTake.ShouldBe(50);
    }

    [Fact]
    public async Task GetRun_ReturnsNotFoundWhenRunIsMissing()
    {
        var controller = CreateController(new RecordingIndexerOperations());

        var result = await controller.GetRun(404, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    private static IndexerController CreateController(RecordingIndexerOperations operations)
    {
        return new IndexerController(operations)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("preferred_username", "Michael")
                    ], "test"))
                }
            }
        };
    }

    private sealed class RecordingIndexerOperations : IIndexerOperationsService
    {
        private static readonly IndexerAcceptedResponse Accepted = new("queued", "Queued.", 77, "/api/indexer/runs/77");

        public string? LastOperation { get; private set; }
        public string? LastUsername { get; private set; }
        public ProcessRequest? LastProcessRequest { get; private set; }
        public string? LastStatusFilter { get; private set; }
        public string? LastOperationFilter { get; private set; }
        public int? LastTake { get; private set; }
        public IReadOnlyList<IndexerRunResponse> Runs { get; set; } = [];

        public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
            string username,
            ProcessRequest request,
            CancellationToken ct = default)
        {
            LastOperation = "process";
            LastUsername = username;
            LastProcessRequest = request;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        {
            LastOperation = "reindex-all";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartDiscoverAsync(
            string username,
            DiscoverRequest? request,
            CancellationToken ct = default)
        {
            LastOperation = "discover";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        {
            LastOperation = "sync-schema";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        {
            LastOperation = "sync-all-schemas";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        {
            LastOperation = "link";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        {
            LastOperation = "detect-communities";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        {
            LastOperation = "link-and-detect";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
            string username,
            string? repo = null,
            CancellationToken ct = default)
        {
            LastOperation = "process-batch-analysis";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
            => Task.FromResult<IndexerRunResponse?>(Runs.FirstOrDefault(run => run.Id == runId));

        public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
            string? status = null,
            string? operation = null,
            int take = 50,
            CancellationToken ct = default)
        {
            LastStatusFilter = status;
            LastOperationFilter = operation;
            LastTake = take;
            return Task.FromResult(Runs);
        }
    }
}
