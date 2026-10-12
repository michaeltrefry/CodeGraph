using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.DatabaseSchema;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class StandaloneIndexerOperationsServiceTests
{
    [Fact]
    public async Task StartReAnalyzeRepositoryAsync_CreatesRetrySafeQueuedRun()
    {
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            runner);

        var accepted = await service.StartReAnalyzeRepositoryAsync("Michael", " SceneWorks ");

        accepted.Status.ShouldBe("queued");
        var run = await runs.GetIndexerRunAsync(accepted.RunId!.Value);
        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.ReAnalyze);
        run.Target.ShouldBe("SceneWorks");
        run.RetrySafe.ShouldBeTrue();
        run.ArgsJson.ShouldContain("SceneWorks");
        runner.EnqueuedRunIds.ShouldBe([accepted.RunId.Value]);
    }

    [Fact]
    public async Task StartSyncSchemaAsync_CreatesQueuedRunForDatabaseSource()
    {
        var sources = new FakeDatabaseSourceStore();
        sources.Seed(new DatabaseSourceEntity
        {
            Id = 17,
            ServerName = "analytics",
            DatabaseName = "warehouse",
            ConnectionString = "Server=analytics;Pwd=secret;",
            Enabled = true
        });
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var service = new StandaloneIndexerOperationsService(runs, sources, runner);

        var accepted = await service.StartSyncSchemaAsync("Michael", 17);

        accepted.Status.ShouldBe("queued");
        accepted.RunId.ShouldBe(1);
        accepted.RunStatusUrl.ShouldBe("/api/indexer/runs/1");
        var run = await runs.GetIndexerRunAsync(1);
        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.SyncSchema);
        run.Target.ShouldBe("17");
        run.RequestedByUsername.ShouldBe("michael");
        run.Message.ShouldNotBeNull();
        run.Message.ShouldNotBeNull();
        run.Message.ShouldContain("analytics/warehouse");
        runner.EnqueuedRunIds.ShouldBe([1]);
    }

    [Fact]
    public async Task GetRunAsync_MapsStoredRun()
    {
        var runs = new FakeIndexerRunStore();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingBackgroundRunner());
        await service.StartSyncAllSchemasAsync("Michael");

        var run = await service.GetRunAsync(1);

        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.SyncAllSchemas);
        run.Status.ShouldBe("queued");
    }

    [Fact]
    public async Task ListRunsAsync_NormalizesFilters_AndReturnsRecentRuns()
    {
        var runs = new FakeIndexerRunStore();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingBackgroundRunner());
        await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncSchema,
            Status = "completed",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncAllSchemas,
            Status = "queued",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncSchema,
            Status = "queued",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var filtered = await service.ListRunsAsync(" QUEUED ", " SYNC_SCHEMA ", take: 10);

        filtered.Count.ShouldBe(1);
        filtered[0].Operation.ShouldBe(IndexerRunOperations.SyncSchema);
        filtered[0].Status.ShouldBe("queued");
    }

    [Fact]
    public async Task StartReIndexAllAsync_CreatesQueuedRunAndEnqueuesBackgroundExecution()
    {
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            runner);

        var accepted = await service.StartReIndexAllAsync("Michael", "reindex-1");

        accepted.Status.ShouldBe("queued");
        accepted.RunId.ShouldBe(1);
        var run = await runs.GetIndexerRunAsync(1);
        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.ReIndexAll);
        run.Target.ShouldBe("all");
        run.RequestedByUsername.ShouldBe("michael");
        runner.EnqueuedRunIds.ShouldBe([1]);
    }

    [Fact]
    public async Task StartProcessRepositoriesAsync_StoresArgsJsonForExecutor()
    {
        var runs = new FakeIndexerRunStore();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingBackgroundRunner());

        await service.StartProcessRepositoriesAsync("Michael", new ProcessRequest
        {
            Repos = ["CodeGraph"],
            ShouldAnalyze = false,
            IncludeAllSource = true
        }, "process-1");

        var run = await runs.GetIndexerRunAsync(1);
        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.ProcessRepositories);
        run.Target.ShouldBe("CodeGraph");
        run.ArgsJson.ShouldNotBeNull();
        run.ArgsJson.ShouldContain("CodeGraph");
        run.ArgsJson.ShouldContain("includeAllSource");
    }

    [Theory]
    [InlineData("""{"repos":["dark-factory-sandbox"],"shouldIndex":true,"shouldAnalyze":false,"skipIfUpToDate":false,"replaceExistingGraph":true}""", true)]
    [InlineData("""{"repos":["dark-factory-sandbox"],"shouldIndex":true,"shouldAnalyze":false,"skipIfUpToDate":false}""", false)]
    public async Task ProcessEndpoint_ReplaceExistingGraphFlowsFromRequestBodyToProcessRepositoryMessage(
        string body, bool expectedReplace)
    {
        // Bind the body as ASP.NET Core does, then follow the request through the indexer host
        // endpoint, the durable run (args JSON) and the executor to the published message.
        var request = JsonSerializer.Deserialize<ProcessRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var runs = new FakeIndexerRunStore();
        var operations = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingBackgroundRunner());
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("preferred_username", "Michael")], "test"))
        };
        httpContext.Request.Headers["Idempotency-Key"] = "process-replace";
        var controller = new CodeGraph.Indexer.Host.Controllers.IndexerController(operations)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpContext }
        };

        var result = await controller.ProcessRepositories(request, CancellationToken.None);
        var accepted = result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.AcceptedResult>()
            .Value.ShouldBeOfType<IndexerAcceptedResponse>();

        var bus = new PublishedMessages();
        var admin = new AdminService(bus, null!, null!, null!, new AllowAllExclusions(), null!, null!);
        var executor = new IndexerRunExecutor(
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            admin,
            new RecordingProjectService());
        await executor.ExecuteAsync(
            new IndexerRunLease((await runs.GetIndexerRunAsync(accepted.RunId!.Value))!, "worker", 1));

        var message = bus.Messages.OfType<CodeGraph.Models.Messages.ProcessRepository>().ShouldHaveSingleItem();
        message.Name.ShouldBe("dark-factory-sandbox");
        message.ReplaceExistingGraph.ShouldBe(expectedReplace);
        message.ShouldAnalyze.ShouldBeFalse();
    }

    [Fact]
    public async Task UnsafeSubmission_RequiresAndDeduplicatesDurableIdentity()
    {
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            runner);
        var request = new ProcessRequest { Repos = ["CodeGraph"], ShouldAnalyze = true };

        await Should.ThrowAsync<ArgumentException>(() => service.StartProcessRepositoriesAsync("Michael", request));

        var first = await service.StartProcessRepositoriesAsync("Michael", request, "request-123");
        var duplicate = await service.StartProcessRepositoriesAsync("Michael", request, "request-123");

        duplicate.RunId.ShouldBe(first.RunId);
        duplicate.Duplicate.ShouldBeTrue();
        duplicate.SubmissionKey.ShouldBe("request-123");
        runner.EnqueuedRunIds.ShouldBe([first.RunId!.Value]);

        var changed = new ProcessRequest { Repos = ["AnotherRepo"], ShouldAnalyze = true };
        await Should.ThrowAsync<IndexerSubmissionConflictException>(() =>
            service.StartProcessRepositoriesAsync("Michael", changed, "request-123"));
    }

    [Fact]
    public async Task ExecuteAsync_RunsSingleSchemaSyncAndMarksRunCompleted()
    {
        var sources = new FakeDatabaseSourceStore();
        sources.Seed(new DatabaseSourceEntity
        {
            Id = 17,
            ServerName = "analytics",
            DatabaseName = "warehouse",
            Enabled = true
        });
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncSchema,
            Target = "17",
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        });
        var schemaExtractor = new RecordingDatabaseSchemaExtractor();
        var executor = new IndexerRunExecutor(sources, schemaExtractor, new RecordingAdminService(), new RecordingProjectService());
        var lease = new IndexerRunLease((await runs.GetIndexerRunAsync(runId))!, "worker", 1);

        var message = await executor.ExecuteAsync(lease);

        message.ShouldContain("analytics/warehouse");
        schemaExtractor.SyncedSources.Select(source => source.Id).ShouldBe([17L]);
    }

    [Fact]
    public async Task ExecuteAsync_MarksRunFailed_WhenOperationIsUnsupported()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = "unsupported",
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        });
        var executor = new IndexerRunExecutor(
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            new RecordingProjectService());
        var lease = new IndexerRunLease((await runs.GetIndexerRunAsync(runId))!, "worker", 1);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteAsync(lease));

        ex.Message.ShouldContain("Unsupported indexer run operation");
    }

    [Fact]
    public async Task ExecuteAsync_RunsReIndexAllThroughAdminServiceAndMarksCompleted()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReIndexAll,
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        });
        var admin = new RecordingAdminService
        {
            ReIndexAllResponse = new ProcessReposResponse(["CodeGraph", "Api"], 2)
        };
        var executor = new IndexerRunExecutor(
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            admin,
            new RecordingProjectService());
        var lease = new IndexerRunLease((await runs.GetIndexerRunAsync(runId))!, "worker", 1);

        var message = await executor.ExecuteAsync(lease);

        admin.ReIndexAllCalls.ShouldBe(1);
        message.ShouldContain("Published 2 repositories");
    }

    [Fact]
    public async Task ExecuteAsync_ReAnalyzesRepositoryAndReportsAnalysisBatch()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReAnalyze,
            Target = "SceneWorks",
            ArgsJson = """{"repo":"SceneWorks"}""",
            Status = "running",
            CreatedAt = DateTime.UtcNow
        });
        var projects = new RecordingProjectService { Batch = CreateBatch("SceneWorks") };
        var executor = new IndexerRunExecutor(
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            projects);
        var lease = new IndexerRunLease((await runs.GetIndexerRunAsync(runId))!, "worker", 1);

        var message = await executor.ExecuteAsync(lease);

        projects.LastReanalyzedRepo.ShouldBe("SceneWorks");
        message.ShouldContain("batch 11");
        message.ShouldContain("pending");
    }

    [Fact]
    public async Task StartBranchOverlayAsync_QueuesRetrySafeRunAndReportsQueuedOverlay()
    {
        var graph = new InMemoryGraphStore();
        graph.AddProject("OverlayRepo");
        var overlayStore = new InMemoryBranchOverlayStore();
        var overlays = new BranchOverlayService(
            overlayStore,
            graph,
            new FixedPathRepoProvider(Path.GetTempPath()),
            new IndexingPipeline(graph, [], Options.Create(new IndexingOptions()), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance),
            new BranchOverlayGit(),
            Options.Create(new BranchOverlayOptions { TtlHours = 48 }),
            NullLogger<BranchOverlayService>.Instance);
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var service = new StandaloneIndexerOperationsService(runs, new FakeDatabaseSourceStore(), runner, overlays);

        var overlay = await service.StartBranchOverlayAsync(
            "Michael",
            new BranchOverlayRequest { Repo = "OverlayRepo", Ref = "factory/feature-x" });

        overlay.OverlayId.ShouldBeGreaterThan(0);
        overlay.Status.ShouldBe(BranchOverlayStatuses.Queued);
        overlay.Ref.ShouldBe("factory/feature-x");
        overlay.StatusUrl.ShouldBe($"/api/indexer/overlays/{overlay.OverlayId}");
        overlay.ExpiresAt.ShouldBe(overlay.CreatedAt.AddHours(48));
        overlay.RunId.ShouldNotBeNull();
        var run = await runs.GetIndexerRunAsync(overlay.RunId!.Value);
        run!.Operation.ShouldBe(IndexerRunOperations.BranchOverlay);
        run.Target.ShouldBe(overlay.OverlayId.ToString());
        run.RetrySafe.ShouldBeTrue();
        runner.EnqueuedRunIds.ShouldBe([overlay.RunId.Value]);

        var status = await service.GetBranchOverlayAsync(overlay.OverlayId);
        status!.Status.ShouldBe(BranchOverlayStatuses.Queued);
        status.RunId.ShouldBe(overlay.RunId);
        (await service.GetBranchOverlayAsync(overlay.OverlayId + 100)).ShouldBeNull();
        await Should.ThrowAsync<KeyNotFoundException>(() => service.StartBranchOverlayAsync(
            "Michael", new BranchOverlayRequest { Repo = "Missing", Ref = "main" }));

        // A run that cannot be queued leaves a failed overlay, not one queued forever.
        runs.CreateFailure = new InvalidOperationException("run store unavailable");
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartBranchOverlayAsync(
            "Michael", new BranchOverlayRequest { Repo = "OverlayRepo", Ref = "factory/feature-y" }));
        var unqueued = overlayStore.All.Single(candidate => candidate.RequestedRef == "factory/feature-y");
        unqueued.Status.ShouldBe(BranchOverlayStatuses.Failed);
        unqueued.Error.ShouldBe("Run could not be queued: run store unavailable");
    }

    [Fact]
    public async Task DurableWorker_RunsBranchOverlayAndDoesNotReplayOverlayFailures()
    {
        var runs = new FakeIndexerRunStore();
        var overlays = new ScriptedBranchOverlayService();
        var succeededRun = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.BranchOverlay,
            Target = "7",
            ArgsJson = "{\"overlayId\":7}",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs, new FakeDatabaseSourceStore(), new RecordingDatabaseSchemaExtractor(), new RecordingAdminService(),
            overlays: overlays);
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();
        (await runs.GetIndexerRunAsync(succeededRun))!.Status.ShouldBe("completed");
        overlays.IndexedOverlayIds.ShouldBe([7L]);

        overlays.Failure = new BranchOverlayIndexingException("bad ref");
        var failedRun = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.BranchOverlay,
            Target = "8",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var failed = await runs.GetIndexerRunAsync(failedRun);
        failed!.Status.ShouldBe("failed");
        failed.ErrorCode.ShouldBe(BranchOverlayIndexingException.FailureCode);
        failed.AttemptCount.ShouldBe(1);
        overlays.IndexedOverlayIds.ShouldBe([7L, 8L]);

        // Transient failures (lock timeout, network) reach the worker unwrapped and are retried.
        overlays.Failure = new TimeoutException("Timed out waiting for the indexing lock.");
        var transientRun = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.BranchOverlay,
            Target = "9",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var retrying = await runs.GetIndexerRunAsync(transientRun);
        retrying!.Status.ShouldBe("queued");
        retrying.ErrorCode.ShouldBe("indexer_operation_failed");
        retrying.AttemptCount.ShouldBe(1);
    }

    private sealed class ScriptedBranchOverlayService : IBranchOverlayService
    {
        public List<long> IndexedOverlayIds { get; } = [];
        public Exception? Failure { get; set; }

        public Task<string> IndexOverlayAsync(long overlayId, CancellationToken ct = default)
        {
            IndexedOverlayIds.Add(overlayId);
            return Failure is null ? Task.FromResult($"overlay {overlayId} ready") : Task.FromException<string>(Failure);
        }

        public Task<BranchOverlayEntity> CreateOverlayAsync(string username, string repo, string gitRef, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BranchOverlayRequestOutcome> RequestOverlayAsync(string username, string repo, string gitRef, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AttachRunAsync(long overlayId, long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkOverlayUnqueuedAsync(BranchOverlayEntity overlay, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BranchOverlayResponse?> GetOverlayAsync(long overlayId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> ExpireOverlaysAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public BranchOverlayResponse Map(BranchOverlayEntity overlay) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DurableWorker_ClaimsPreexistingQueuedRunAndCompletesIt()
    {
        var runs = new FakeIndexerRunStore();
        var sources = new FakeDatabaseSourceStore();
        sources.Seed(new DatabaseSourceEntity { Id = 17, ServerName = "analytics", DatabaseName = "warehouse" });
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncSchema,
            Target = "17",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(runs, sources, new RecordingDatabaseSchemaExtractor(), new RecordingAdminService());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var completed = await runs.GetIndexerRunAsync(runId);
        completed!.Status.ShouldBe("completed");
        completed.AttemptCount.ShouldBe(1);
        completed.Message.ShouldNotBeNull();
        completed.Message.ShouldContain("analytics/warehouse");
    }

    [Fact]
    public async Task DurableWorker_RetriesSchemaSyncWhenExtractorReportsPartialFailure()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncAllSchemas,
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new FailingDatabaseSchemaExtractor(),
            new RecordingAdminService());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var retrying = await runs.GetIndexerRunAsync(runId);
        retrying.ShouldNotBeNull();
        retrying.Status.ShouldBe("queued");
        retrying.AttemptCount.ShouldBe(1);
        retrying.NextAttemptAt.ShouldNotBeNull();
        retrying.ErrorCode.ShouldBe("indexer_operation_failed");
        retrying.Error.ShouldBe("partial schema sync failure");
    }

    [Fact]
    public async Task DurableWorker_FailsRustSemanticReAnalyzeWithoutResourceIntensiveRetry()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReAnalyze,
            Target = "SceneWorks",
            ArgsJson = """{"repo":"SceneWorks"}""",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            new FailingProjectService(new RustSemanticIndexingException(
                "rust_semantic_command_timeout",
                "rust-analyzer timed out")));
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var failed = await runs.GetIndexerRunAsync(runId);
        failed!.Status.ShouldBe("failed");
        failed.AttemptCount.ShouldBe(1);
        failed.NextAttemptAt.ShouldBeNull();
        failed.ErrorCode.ShouldBe("rust_semantic_command_timeout");
        failed.Error.ShouldBe("rust-analyzer timed out");
    }

    [Fact]
    public async Task DurableWorker_CompletesReAnalyzeWithRustFallbackFailureCodeAndCrates()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReAnalyze,
            Target = "SceneWorks",
            ArgsJson = """{"repo":"SceneWorks"}""",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            configure: collection => collection.AddScoped<IProjectService, RustFallbackProjectService>());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var completed = await runs.GetIndexerRunAsync(runId);
        completed!.Status.ShouldBe("completed");
        completed.AttemptCount.ShouldBe(1);
        completed.ErrorCode.ShouldBe("rust_semantic_fallback_timeout");
        completed.Message.ShouldNotBeNull();
        completed.Message.ShouldContain("tree-sitter");
        completed.Message.ShouldContain("alpha, beta");
        using var detail = JsonDocument.Parse(completed.Error!);
        var fallback = detail.RootElement.EnumerateArray().Single();
        fallback.GetProperty("failureCode").GetString().ShouldBe("timeout");
        fallback.GetProperty("detailCode").GetString().ShouldBe("rust_semantic_command_timeout");
        fallback.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString())
            .ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task DurableWorker_CleanRustSemanticRun_CompletesWithOutcomeSummaryAndNoErrorCode()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReAnalyze,
            Target = "SceneWorks",
            ArgsJson = """{"repo":"SceneWorks"}""",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            configure: collection => collection.AddScoped<IProjectService, RustOutcomesProjectService>());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var completed = await runs.GetIndexerRunAsync(runId);
        completed!.Status.ShouldBe("completed");
        completed.ErrorCode.ShouldBeNull();
        completed.Error.ShouldBeNull();
        completed.Message.ShouldNotBeNull();
        completed.Message.ShouldEndWith(
            " Rust semantic: 2 groups (2 semantic, 0 fallback, 0 skipped), 5 crates, 1834s.");
    }

    [Fact]
    public async Task DurableWorker_SeveralCrateGroupFallbacks_RecordsMixedCodeAndEachGroupInOrder()
    {
        var runs = new FakeIndexerRunStore();
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ReAnalyze,
            Target = "SceneWorks",
            ArgsJson = """{"repo":"SceneWorks"}""",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            configure: collection => collection.AddScoped<IProjectService, CrateGroupRustFallbackProjectService>());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var completed = await runs.GetIndexerRunAsync(runId);
        completed!.Status.ShouldBe("completed");
        completed.ErrorCode.ShouldBe("rust_semantic_fallback_mixed");
        using var detail = JsonDocument.Parse(completed.Error!);
        var fallbacks = detail.RootElement.EnumerateArray().ToList();
        fallbacks.Select(fallback => fallback.GetProperty("failureCode").GetString())
            .ShouldBe(["timeout", "resource_exhausted"]);
        fallbacks.Select(fallback => string.Join(",", fallback.GetProperty("crates").EnumerateArray()
                .Select(crate => crate.GetString())))
            .ShouldBe(["beta", "gamma,zeta"]);
    }

    [Fact]
    public void DegradationDetail_HugeFallbackMessage_FitsIndexerRunErrorColumn()
    {
        // 70,000 two-byte characters: ~140 KB of UTF-8, well over the 65,535-byte TEXT column.
        var hugeMessage = new string('\u00e9', 70_000);
        var detail = IndexerRunBackgroundRunner.SerializeDegradationDetail(
        [
            new RustSemanticFallback("Cargo.toml", ["alpha", "beta"], "other", "rust_analyzer_scip_failed", hugeMessage)
        ]);

        System.Text.Encoding.UTF8.GetByteCount(detail)
            .ShouldBeLessThanOrEqualTo(IndexerRunBackgroundRunner.MaxDegradationDetailBytes);
        using var parsed = JsonDocument.Parse(detail);
        var fallback = parsed.RootElement.EnumerateArray().Single();
        fallback.GetProperty("failureCode").GetString().ShouldBe("other");
        fallback.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString())
            .ShouldBe(["alpha", "beta"]);
        fallback.GetProperty("message").GetString()!.ShouldStartWith(new string('\u00e9', 100));
    }

    [Fact]
    public async Task DurableWorker_RetriesOnlySafeOperationsAndExposesAttemptState()
    {
        var runs = new FakeIndexerRunStore();
        var safeId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.SyncSchema,
            Target = "404",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow
        });
        var unsafeId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = "unsupported-publication",
            Status = "queued",
            RetrySafe = false,
            CreatedAt = DateTime.UtcNow.AddSeconds(1)
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();
        var retrying = await runs.GetIndexerRunAsync(safeId);
        retrying!.Status.ShouldBe("queued");
        retrying.AttemptCount.ShouldBe(1);
        retrying.NextAttemptAt.ShouldNotBeNull();

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();
        var failed = await runs.GetIndexerRunAsync(unsafeId);
        failed!.Status.ShouldBe("failed");
        failed.AttemptCount.ShouldBe(1);
        failed.Error.ShouldNotBeNull();
        failed.Error.ShouldContain("Unsupported indexer run operation");
    }

    [Fact]
    public async Task CancelRunAsync_CancelsQueuedRunBeforeAnyOwnerCanClaimIt()
    {
        var runs = new FakeIndexerRunStore();
        var service = new StandaloneIndexerOperationsService(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingBackgroundRunner());
        var accepted = await service.StartReIndexAllAsync("Michael", "cancel-1");

        var canceled = await service.CancelRunAsync(accepted.RunId!.Value);

        canceled!.Status.ShouldBe("canceled");
        canceled.CancelRequestedAt.ShouldNotBeNull();
        canceled.CompletedAt.ShouldNotBeNull();
        (await runs.TryClaimNextIndexerRunAsync("worker", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1))).ShouldBeNull();
    }

    [Fact]
    public async Task DurableWorker_CoalescesConcurrentWakeSignalsWithoutThrowing()
    {
        var runs = new FakeIndexerRunStore();
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService());
        var worker = CreateWorker(services);

        await Task.WhenAll(Enumerable.Range(1, 100).Select(id => worker.EnqueueAsync(id)));
    }

    [Fact]
    public async Task DurableWorker_CancelsCooperativeExecutionWhenHeartbeatLosesOwnership()
    {
        var runs = new FakeIndexerRunStore { LoseLeaseOnRenewal = true };
        var runId = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ProcessRepositories,
            ArgsJson = """{"repos":["CodeGraph"]}""",
            Status = "queued",
            RetrySafe = false,
            CreatedAt = DateTime.UtcNow
        });
        using var services = CreateWorkerServices(
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new BlockingAdminService());
        var worker = CreateWorker(services);

        (await worker.TryExecuteNextAsync(CancellationToken.None)).ShouldBeTrue();

        var abandoned = await runs.GetIndexerRunAsync(runId);
        abandoned!.Status.ShouldBe("running");
        abandoned.AttemptCount.ShouldBe(1);
        abandoned.ExecutionOwner.ShouldNotBeNull();
        abandoned.LeaseExpiresAt.ShouldNotBeNull();
    }

    private static ServiceProvider CreateWorkerServices(
        IIndexerRunStore runs,
        IDatabaseSourceStore sources,
        IDatabaseSchemaExtractor schemas,
        IAdminService admin,
        IProjectService? projects = null,
        Action<IServiceCollection>? configure = null,
        IBranchOverlayService? overlays = null)
    {
        var configureProjects = configure is not null;
        var services = new ServiceCollection();
        if (overlays is not null)
            services.AddSingleton(overlays);
        services.AddSingleton(runs);
        services.AddSingleton(sources);
        services.AddSingleton(schemas);
        services.AddSingleton(admin);
        if (projects is not null || !configureProjects)
            services.AddSingleton(projects ?? new RecordingProjectService());
        services.AddScoped<IndexingRunDiagnostics>();
        services.AddTransient<IndexerRunExecutor>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static IndexerRunBackgroundRunner CreateWorker(ServiceProvider services)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new IndexerRunWorkerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                HeartbeatInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromMilliseconds(100),
                RetryDelay = TimeSpan.FromMinutes(1),
                MaxAttempts = 3
            }),
            NullLogger<IndexerRunBackgroundRunner>.Instance);

    internal sealed class FakeDatabaseSourceStore : IDatabaseSourceStore
    {
        private readonly Dictionary<long, DatabaseSourceEntity> _sources = new();

        public void Seed(DatabaseSourceEntity source) => _sources[source.Id] = source;
        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() => Task.FromResult<IReadOnlyList<DatabaseSourceEntity>>(_sources.Values.ToList());
        public Task<DatabaseSourceEntity?> GetAsync(long id) => Task.FromResult(_sources.GetValueOrDefault(id));
        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateMcpExposureAsync(long id, bool? mcpHubEnabled, string? mcpExposureMode, string? mcpDisplayName, string? mcpEnvironment) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();
        public Task UpdateLastSyncedAsync(long id) => Task.CompletedTask;
    }

    internal sealed class RecordingBackgroundRunner : IIndexerRunBackgroundRunner
    {
        public List<long> EnqueuedRunIds { get; } = [];

        public Task EnqueueAsync(long runId, CancellationToken ct = default)
        {
            EnqueuedRunIds.Add(runId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDatabaseSchemaExtractor : IDatabaseSchemaExtractor
    {
        public List<DatabaseSourceEntity> SyncedSources { get; } = [];
        public int SyncAllCalls { get; private set; }

        public Task SyncAsync(DatabaseSourceEntity source, CancellationToken ct = default)
        {
            SyncedSources.Add(source);
            return Task.CompletedTask;
        }

        public Task SyncAllAsync(CancellationToken ct = default)
        {
            SyncAllCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDatabaseSchemaExtractor : IDatabaseSchemaExtractor
    {
        public Task SyncAsync(DatabaseSourceEntity source, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("partial schema sync failure"));

        public Task SyncAllAsync(CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("partial schema sync failure"));
    }

    private sealed class BlockingAdminService : IAdminService
    {
        public async Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The blocking operation unexpectedly resumed.");
        }

        public Task<ProcessReposResponse> ReIndexAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task LinkAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DetectCommunitiesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task LinkAndDetectAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ProcessBatchAnalysisAsync(string? repo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAdminService : IAdminService
    {
        public int ReIndexAllCalls { get; private set; }
        public ProcessReposResponse ReIndexAllResponse { get; set; } = new([], 0);

        public Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request, CancellationToken ct = default)
            => Task.FromResult(new ProcessReposResponse(request.Repos, request.Repos.Count));

        public Task<ProcessReposResponse> ReIndexAllAsync(CancellationToken ct = default)
        {
            ReIndexAllCalls++;
            return Task.FromResult(ReIndexAllResponse);
        }

        public Task LinkAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DetectCommunitiesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LinkAndDetectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProcessBatchAnalysisAsync(string? repo, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request, CancellationToken ct = default)
            => Task.FromResult(new DiscoverResponse(0, 0, 0, 0, 0, []));
    }

    private sealed class PublishedMessages : CodeGraph.Services.Messaging.IMessageBus
    {
        public List<object> Messages { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAllExclusions : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) => Task.FromResult<string?>(null);
        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) => Task.FromResult(new HashSet<string>());
        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() => Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);
        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) => throw new NotSupportedException();
        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();
        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) => Task.CompletedTask;
    }

    private sealed class RecordingProjectService : IProjectService
    {
        public AnalysisBatchResponse? Batch { get; set; }
        public string? LastReanalyzedRepo { get; private set; }

        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(
            string repo,
            CancellationToken cancellationToken = default)
        {
            LastReanalyzedRepo = repo;
            return Task.FromResult(Batch);
        }

        public Task ProcessRepository(
            CodeGraph.Models.Messages.ProcessRepository message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo)
            => throw new NotSupportedException();
    }

    private sealed class RustFallbackProjectService(IndexingRunDiagnostics diagnostics) : IProjectService
    {
        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = default)
        {
            diagnostics.RecordRustSemanticFallback(new RustSemanticFallback(
                "Cargo.toml",
                ["alpha", "beta"],
                "timeout",
                "rust_semantic_command_timeout",
                "rust-analyzer timed out"));
            return Task.FromResult<AnalysisBatchResponse?>(CreateBatch(repo));
        }

        public Task ProcessRepository(CodeGraph.Models.Messages.ProcessRepository message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo)
            => throw new NotSupportedException();
    }

    private sealed class RustOutcomesProjectService(IndexingRunDiagnostics diagnostics) : IProjectService
    {
        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = default)
        {
            diagnostics.RecordRustSemanticOutcomes(new RustSemanticOutcomes(
                "Cargo.toml", 5, 2, 4, 2, 0, 0, 1_834_200,
                new RustSemanticSettingsSnapshot(4, 600, 3600, 8192, false, false),
                [
                    new RustSemanticGroupOutcome(0, ["a", "b", "c", "d"], "semantic", null, null, 1_000_000, 512, 40, 900, 1200),
                    new RustSemanticGroupOutcome(1, ["e"], "semantic", null, null, 834_200, 256, 10, 300, 400)
                ]));
            return Task.FromResult<AnalysisBatchResponse?>(CreateBatch(repo));
        }

        public Task ProcessRepository(CodeGraph.Models.Messages.ProcessRepository message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo)
            => throw new NotSupportedException();
    }

    private sealed class CrateGroupRustFallbackProjectService(IndexingRunDiagnostics diagnostics) : IProjectService
    {
        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = default)
        {
            diagnostics.RecordRustSemanticFallback(new RustSemanticFallback(
                "Cargo.toml", ["beta"], "timeout", "rust_semantic_group_timeout", "group timed out"));
            diagnostics.RecordRustSemanticFallback(new RustSemanticFallback(
                "Cargo.toml", ["gamma", "zeta"], "resource_exhausted", "rust_semantic_memory_limit_exceeded",
                "group exceeded its memory limit"));
            return Task.FromResult<AnalysisBatchResponse?>(CreateBatch(repo));
        }

        public Task ProcessRepository(CodeGraph.Models.Messages.ProcessRepository message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo)
            => throw new NotSupportedException();
    }

    private sealed class FailingProjectService(Exception failure) : IProjectService
    {
        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = default)
            => Task.FromException<AnalysisBatchResponse?>(failure);

        public Task ProcessRepository(CodeGraph.Models.Messages.ProcessRepository message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo)
            => throw new NotSupportedException();
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

    internal sealed class FakeIndexerRunStore : IIndexerRunStore
    {
        private readonly Dictionary<long, IndexerRunEntity> _runs = new();
        private long _nextId = 1;
        public bool LoseLeaseOnRenewal { get; init; }

        /// <summary>When set, creating a run throws this (simulates a database error).</summary>
        public Exception? CreateFailure { get; set; }

        public CancellationToken? LastCreateToken { get; private set; }

        public Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default)
        {
            LastCreateToken = ct;
            if (CreateFailure is not null)
                return Task.FromException<long>(CreateFailure);
            run.Id = _nextId++;
            _runs[run.Id] = Clone(run);
            return Task.FromResult(run.Id);
        }

        public async Task<IndexerRunSubmissionResult> CreateOrGetIndexerRunAsync(
            IndexerRunEntity run,
            CancellationToken ct = default)
        {
            var existing = _runs.Values.FirstOrDefault(candidate =>
                candidate.RequestedByUsername == run.RequestedByUsername &&
                candidate.SubmissionKey == run.SubmissionKey &&
                run.SubmissionKey is not null);
            if (existing is not null)
                return new IndexerRunSubmissionResult(existing.Id, Created: false);

            return new IndexerRunSubmissionResult(await CreateIndexerRunAsync(run, ct), Created: true);
        }

        public Task UpdateIndexerRunStatusAsync(
            long runId,
            string status,
            string? message = null,
            DateTime? completedAt = null,
            string? error = null,
            CancellationToken ct = default)
        {
            if (!_runs.TryGetValue(runId, out var run))
                throw new InvalidOperationException($"Run {runId} was not found.");

            run.Status = status;
            run.Message = message ?? run.Message;
            run.Error = error;
            if (status == "running")
                run.StartedAt ??= DateTime.UtcNow;
            if (completedAt is not null)
                run.CompletedAt = completedAt;
            return Task.CompletedTask;
        }

        public Task<IndexerRunEntity?> GetIndexerRunAsync(long runId, CancellationToken ct = default)
            => Task.FromResult(_runs.TryGetValue(runId, out var run) ? Clone(run) : null);

        public Task<IReadOnlyList<IndexerRunEntity>> ListIndexerRunsAsync(
            string? status = null,
            string? operation = null,
            int take = 50,
            CancellationToken ct = default)
        {
            var query = _runs.Values.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(run => run.Status == status);

            if (!string.IsNullOrWhiteSpace(operation))
                query = query.Where(run => run.Operation == operation);

            return Task.FromResult<IReadOnlyList<IndexerRunEntity>>(query
                .OrderByDescending(run => run.CreatedAt)
                .ThenByDescending(run => run.Id)
                .Take(Math.Clamp(take, 1, 200))
                .Select(Clone)
                .ToList());
        }

        public Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(string owner, DateTime now, DateTime leaseExpiresAt, CancellationToken ct = default)
        {
            var run = _runs.Values
                .Where(candidate => candidate.Status == "queued" && (candidate.NextAttemptAt is null || candidate.NextAttemptAt <= now))
                .OrderBy(candidate => candidate.CreatedAt)
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefault();
            if (run is null)
                return Task.FromResult<IndexerRunLease?>(null);
            run.Status = "running";
            run.ExecutionOwner = owner;
            run.LeaseExpiresAt = leaseExpiresAt;
            run.HeartbeatAt = now;
            run.AttemptCount++;
            run.FencingToken++;
            return Task.FromResult<IndexerRunLease?>(new IndexerRunLease(Clone(run), owner, run.FencingToken));
        }

        public Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
            string owner,
            DateTime now,
            DateTime leaseExpiresAt,
            int maxAttempts,
            CancellationToken ct = default)
            => TryClaimNextIndexerRunAsync(owner, now, leaseExpiresAt, ct);

        public Task<IndexerRunLeaseRenewal> RenewIndexerRunLeaseAsync(long runId, string owner, long fencingToken, DateTime now, DateTime leaseExpiresAt, CancellationToken ct = default)
        {
            if (LoseLeaseOnRenewal)
                return Task.FromResult(new IndexerRunLeaseRenewal(false, false));

            var owned = _runs.TryGetValue(runId, out var run)
                && run.ExecutionOwner == owner
                && run.FencingToken == fencingToken
                && run.Status == "running";
            if (owned)
            {
                run!.HeartbeatAt = now;
                run.LeaseExpiresAt = leaseExpiresAt;
            }
            return Task.FromResult(new IndexerRunLeaseRenewal(owned, owned && run!.CancelRequestedAt is not null));
        }

        public Task<bool> CompleteIndexerRunAsync(IndexerRunLease lease, string message, DateTime completedAt, CancellationToken ct = default)
            => FinishAsync(lease, "completed", message, completedAt);

        public Task<bool> CompleteIndexerRunAsync(IndexerRunLease lease, string message, string? errorCode, string? error, DateTime completedAt, CancellationToken ct = default)
            => FinishAsync(lease, "completed", message, completedAt, errorCode, error);

        public Task<IndexerRunFailureDisposition> FailOrRetryIndexerRunAsync(IndexerRunLease lease, string errorCode, string error, DateTime now, DateTime nextAttemptAt, int maxAttempts, CancellationToken ct = default)
        {
            if (!Owns(lease))
                return Task.FromResult(IndexerRunFailureDisposition.LeaseLost);
            var run = _runs[lease.Run.Id];
            run.ErrorCode = errorCode;
            run.Error = error;
            run.ExecutionOwner = null;
            run.LeaseExpiresAt = null;
            if (run.RetrySafe && run.AttemptCount < maxAttempts)
            {
                run.Status = "queued";
                run.NextAttemptAt = nextAttemptAt;
                return Task.FromResult(IndexerRunFailureDisposition.Retrying);
            }
            run.Status = "failed";
            run.CompletedAt = now;
            return Task.FromResult(IndexerRunFailureDisposition.Failed);
        }

        public Task<bool> CancelOwnedIndexerRunAsync(IndexerRunLease lease, string message, DateTime completedAt, CancellationToken ct = default)
            => FinishAsync(lease, "canceled", message, completedAt);

        public Task<IndexerRunEntity?> RequestIndexerRunCancellationAsync(long runId, DateTime requestedAt, CancellationToken ct = default)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return Task.FromResult<IndexerRunEntity?>(null);
            run.CancelRequestedAt = requestedAt;
            if (run.Status == "queued")
            {
                run.Status = "canceled";
                run.CompletedAt = requestedAt;
            }
            return Task.FromResult<IndexerRunEntity?>(Clone(run));
        }

        private Task<bool> FinishAsync(
            IndexerRunLease lease,
            string status,
            string message,
            DateTime completedAt,
            string? errorCode = null,
            string? error = null)
        {
            if (!Owns(lease))
                return Task.FromResult(false);
            var run = _runs[lease.Run.Id];
            run.Status = status;
            run.Message = message;
            run.ErrorCode = errorCode;
            run.Error = error;
            run.CompletedAt = completedAt;
            run.ExecutionOwner = null;
            run.LeaseExpiresAt = null;
            return Task.FromResult(true);
        }

        private bool Owns(IndexerRunLease lease)
            => _runs.TryGetValue(lease.Run.Id, out var run)
                && run.Status == "running"
                && run.ExecutionOwner == lease.Owner
                && run.FencingToken == lease.FencingToken;

        private static IndexerRunEntity Clone(IndexerRunEntity run) => new()
        {
            Id = run.Id,
            Operation = run.Operation,
            Status = run.Status,
            RequestedByUsername = run.RequestedByUsername,
            Target = run.Target,
            ArgsJson = run.ArgsJson,
            Message = run.Message,
            ErrorCode = run.ErrorCode,
            Error = run.Error,
            CreatedAt = run.CreatedAt,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            ExecutionOwner = run.ExecutionOwner,
            LeaseExpiresAt = run.LeaseExpiresAt,
            HeartbeatAt = run.HeartbeatAt,
            CancelRequestedAt = run.CancelRequestedAt,
            NextAttemptAt = run.NextAttemptAt,
            AttemptCount = run.AttemptCount,
            FencingToken = run.FencingToken,
            RetrySafe = run.RetrySafe,
            SubmissionKey = run.SubmissionKey,
            SubmissionHash = run.SubmissionHash
        };
    }
}
