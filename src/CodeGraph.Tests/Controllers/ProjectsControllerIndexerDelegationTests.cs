using System.Security.Claims;
using CodeGraph.Api.Controllers;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class ProjectsControllerIndexerDelegationTests
{
    [Fact]
    public async Task ReAnalyze_DelegatesToConfiguredIndexerOperations()
    {
        var operations = new RecordingIndexerOperations
        {
            Batch = CreateBatch("SceneWorks")
        };
        var controller = new ProjectsController(null!, null!, null!, operations)
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

        var result = await controller.ReAnalyze(
            new ReAnalyzeRequest { Repo = "SceneWorks" },
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(operations.Batch);
        operations.LastUsername.ShouldBe("Michael");
        operations.LastRepo.ShouldBe("SceneWorks");
    }

    private sealed class RecordingIndexerOperations : IIndexerOperationsService
    {
        public AnalysisBatchResponse? Batch { get; set; }
        public string? LastUsername { get; private set; }
        public string? LastRepo { get; private set; }

        public Task<AnalysisBatchResponse?> ReAnalyzeRepositoryAsync(
            string username,
            string repo,
            CancellationToken ct = default)
        {
            LastUsername = username;
            LastRepo = repo;
            return Task.FromResult(Batch);
        }

        public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunResponse?> CancelRunAsync(long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(string? status = null, string? operation = null, int take = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static AnalysisBatchResponse CreateBatch(string repo) => new(
        11,
        repo,
        "batch-11",
        "anthropic",
        "batch",
        true,
        "pending",
        2,
        0,
        DateTime.UtcNow,
        null);
}
