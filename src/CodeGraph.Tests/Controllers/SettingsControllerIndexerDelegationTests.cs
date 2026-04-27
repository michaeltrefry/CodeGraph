using System.Security.Claims;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class SettingsControllerIndexerDelegationTests
{
    [Fact]
    public async Task ProcessRepositories_DelegatesToIndexerOperations()
    {
        var indexer = new RecordingIndexerOperationsService();
        var controller = CreateController(indexer);
        var request = new ProcessRequest { Repos = ["CodeGraph"], ShouldIndex = true };

        var result = await controller.ProcessRepositories(request, CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        accepted.Location.ShouldBe("/api/indexer/runs/77");
        accepted.Value.ShouldBeOfType<IndexerAcceptedResponse>().RunId.ShouldBe(77);
        indexer.LastOperation.ShouldBe("process");
        indexer.LastUsername.ShouldBe("Michael");
        indexer.LastProcessRequest.ShouldBe(request);
    }

    [Fact]
    public async Task LegacyOperations_ReturnAcceptedIndexerRun()
    {
        var indexer = new RecordingIndexerOperationsService();
        var controller = CreateController(indexer);

        var result = await controller.LinkAndDetect(CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        accepted.Location.ShouldBe("/api/indexer/runs/77");
        accepted.Value.ShouldBeOfType<IndexerAcceptedResponse>().RunStatusUrl.ShouldBe("/api/indexer/runs/77");
        indexer.LastOperation.ShouldBe("link-and-detect");
        indexer.LastUsername.ShouldBe("Michael");
    }

    private static SettingsController CreateController(RecordingIndexerOperationsService indexer)
    {
        return new SettingsController(
            indexer,
            new ThrowingJobScheduleService(),
            new ThrowingDbHealthStore(),
            new ThrowingExclusionService(),
            new ThrowingWikiService(),
            new ThrowingMcpDocService())
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

    private sealed class RecordingIndexerOperationsService : IIndexerOperationsService
    {
        private static readonly IndexerAcceptedResponse Accepted = new("queued", "Queued.", 77, "/api/indexer/runs/77");

        public string? LastOperation { get; private set; }
        public string? LastUsername { get; private set; }
        public ProcessRequest? LastProcessRequest { get; private set; }

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

        public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default)
        {
            LastOperation = "discover";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
            => throw new NotSupportedException();

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
            LastOperation = "batch-analysis";
            LastUsername = username;
            return Task.FromResult(Accepted);
        }

        public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
            string? status = null,
            string? operation = null,
            int take = 50,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingJobScheduleService : IJobScheduleService
    {
        public Task<IReadOnlyList<JobScheduleResponse>> ListAsync() => throw new NotSupportedException();
        public Task<JobScheduleResponse?> GetAsync(long id) => throw new NotSupportedException();
        public Task<JobScheduleResponse> CreateAsync(CreateJobScheduleRequest request) => throw new NotSupportedException();
        public Task<JobScheduleResponse?> UpdateAsync(long id, UpdateJobScheduleRequest request) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();
        public Task<JobScheduleResponse?> SetEnabledAsync(long id, bool isEnabled) => throw new NotSupportedException();
        public Task<JobExecutionResponse?> RunNowAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryRunNextDueScheduleAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingDbHealthStore : IDbHealthStore
    {
        public Task<DatabaseHealthResponse> GetDatabaseHealthAsync() => throw new NotSupportedException();
    }

    private sealed class ThrowingExclusionService : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) => throw new NotSupportedException();
        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() => throw new NotSupportedException();
        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) => throw new NotSupportedException();
        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();
        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) => throw new NotSupportedException();
    }

    private sealed class ThrowingWikiService : IWikiService
    {
        public Task<IReadOnlyList<WikiSectionResponse>> ListSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionResponse?> GetSectionAsync(string sectionSlug) => throw new NotSupportedException();
        public Task<WikiSectionResponse?> CreateSectionAsync(WikiSectionRequest request) => throw new NotSupportedException();
        public Task<WikiSectionResponse?> UpdateSectionAsync(long id, WikiSectionRequest request) => throw new NotSupportedException();
        public Task<bool> DeleteSectionAsync(long id) => throw new NotSupportedException();
        public Task<List<WikiTreeNode>> GetSectionTreeAsync(string sectionSlug) => throw new NotSupportedException();
        public Task<WikiPageResponse?> GetPageAsync(string sectionSlug, string path) => throw new NotSupportedException();
        public Task<WikiPageListItem?> CreatePageAsync(string sectionSlug, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<WikiPageListItem?> CreateChildPageAsync(string sectionSlug, string parentPath, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<WikiPageListItem?> UpdatePageAsync(string sectionSlug, string path, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<bool> DeletePageAsync(string sectionSlug, string path) => throw new NotSupportedException();
        public Task<bool> MovePageAsync(string sectionSlug, string path, WikiPageMoveRequest request) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiRevisionListItem>> GetRevisionsAsync(string sectionSlug, string path) => throw new NotSupportedException();
        public Task<WikiRevisionResponse?> GetRevisionAsync(string sectionSlug, string path, int revision) => throw new NotSupportedException();
    }

    private sealed class ThrowingMcpDocService : IMcpDocService
    {
        public Task RegenerateAsync() => throw new NotSupportedException();
    }
}
