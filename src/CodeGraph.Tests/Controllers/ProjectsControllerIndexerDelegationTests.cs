using System.Security.Claims;
using System.Reflection;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Controllers;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class ProjectsControllerIndexerDelegationTests
{
    [Fact]
    public void Delete_RequiresAdminPolicy()
    {
        var method = typeof(ProjectsController).GetMethod(
            nameof(ProjectsController.Delete),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(string)]);

        method.ShouldNotBeNull();
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        authorize.ShouldNotBeNull();
        authorize.Policy.ShouldBe(CodeGraphAuthenticationDefaults.AdminPolicy);
    }

    [Fact]
    public async Task ReAnalyze_DelegatesToConfiguredIndexerOperations()
    {
        var operations = new RecordingIndexerOperations();
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
        controller.Request.Headers["Idempotency-Key"] = "reanalyze-77";

        var result = await controller.ReAnalyze(
            new ReAnalyzeRequest { Repo = "SceneWorks" },
            CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        accepted.Value.ShouldBe(operations.Accepted);
        operations.LastUsername.ShouldBe("Michael");
        operations.LastRepo.ShouldBe("SceneWorks");
        operations.LastSubmissionKey.ShouldBe("reanalyze-77");
    }

    private sealed class RecordingIndexerOperations : IIndexerOperationsService
    {
        public IndexerAcceptedResponse Accepted { get; } = new("queued", "Queued.", 77, "/api/indexer/runs/77");
        public string? LastUsername { get; private set; }
        public string? LastRepo { get; private set; }
        public string? LastSubmissionKey { get; private set; }

        public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(
            string username,
            string repo,
            CancellationToken ct = default)
        {
            LastUsername = username;
            LastRepo = repo;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(
            string username,
            string repo,
            string? submissionKey,
            CancellationToken ct = default)
        {
            LastSubmissionKey = submissionKey;
            return StartReAnalyzeRepositoryAsync(username, repo, ct);
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

}
