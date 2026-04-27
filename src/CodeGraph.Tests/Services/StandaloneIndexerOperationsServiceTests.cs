using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.DatabaseSchema;
using CodeGraph.Services.Indexer;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class StandaloneIndexerOperationsServiceTests
{
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
        var service = new StandaloneIndexerOperationsService(runs, new FakeDatabaseSourceStore(), new RecordingBackgroundRunner());
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
        var service = new StandaloneIndexerOperationsService(runs, new FakeDatabaseSourceStore(), new RecordingBackgroundRunner());
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
        var service = new StandaloneIndexerOperationsService(runs, new FakeDatabaseSourceStore(), runner);

        var accepted = await service.StartReIndexAllAsync("Michael");

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
        var service = new StandaloneIndexerOperationsService(runs, new FakeDatabaseSourceStore(), new RecordingBackgroundRunner());

        await service.StartProcessRepositoriesAsync("Michael", new ProcessRequest
        {
            Repos = ["CodeGraph"],
            ShouldAnalyze = false,
            IncludeAllSource = true
        });

        var run = await runs.GetIndexerRunAsync(1);
        run.ShouldNotBeNull();
        run.Operation.ShouldBe(IndexerRunOperations.ProcessRepositories);
        run.Target.ShouldBe("CodeGraph");
        run.ArgsJson.ShouldNotBeNull();
        run.ArgsJson.ShouldContain("CodeGraph");
        run.ArgsJson.ShouldContain("includeAllSource");
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
        var executor = new IndexerRunExecutor(
            runs,
            sources,
            schemaExtractor,
            new RecordingAdminService(),
            NullLogger<IndexerRunExecutor>.Instance);

        await executor.ExecuteAsync(runId);

        var run = await runs.GetIndexerRunAsync(runId);
        run.ShouldNotBeNull();
        run.Status.ShouldBe("completed");
        run.CompletedAt.ShouldNotBeNull();
        run.Message.ShouldNotBeNull();
        run.Message.ShouldContain("analytics/warehouse");
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
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            new RecordingAdminService(),
            NullLogger<IndexerRunExecutor>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteAsync(runId));

        ex.Message.ShouldContain("Unsupported indexer run operation");
        var run = await runs.GetIndexerRunAsync(runId);
        run.ShouldNotBeNull();
        run.Status.ShouldBe("failed");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("Unsupported indexer run operation");
        run.CompletedAt.ShouldNotBeNull();
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
            runs,
            new FakeDatabaseSourceStore(),
            new RecordingDatabaseSchemaExtractor(),
            admin,
            NullLogger<IndexerRunExecutor>.Instance);

        await executor.ExecuteAsync(runId);

        admin.ReIndexAllCalls.ShouldBe(1);
        var run = await runs.GetIndexerRunAsync(runId);
        run.ShouldNotBeNull();
        run.Status.ShouldBe("completed");
        run.Message.ShouldNotBeNull();
        run.Message.ShouldContain("Published 2 repositories");
    }

    private sealed class FakeDatabaseSourceStore : IDatabaseSourceStore
    {
        private readonly Dictionary<long, DatabaseSourceEntity> _sources = new();

        public void Seed(DatabaseSourceEntity source) => _sources[source.Id] = source;
        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() => Task.FromResult<IReadOnlyList<DatabaseSourceEntity>>(_sources.Values.ToList());
        public Task<DatabaseSourceEntity?> GetAsync(long id) => Task.FromResult(_sources.GetValueOrDefault(id));
        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();
        public Task UpdateLastSyncedAsync(long id) => Task.CompletedTask;
    }

    private sealed class RecordingBackgroundRunner : IIndexerRunBackgroundRunner
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

    private sealed class RecordingAdminService : IAdminService
    {
        public int ReIndexAllCalls { get; private set; }
        public ProcessReposResponse ReIndexAllResponse { get; set; } = new([], 0);

        public Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request)
            => Task.FromResult(new ProcessReposResponse(request.Repos, request.Repos.Count));

        public Task<ProcessReposResponse> ReIndexAllAsync()
        {
            ReIndexAllCalls++;
            return Task.FromResult(ReIndexAllResponse);
        }

        public Task LinkAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DetectCommunitiesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LinkAndDetectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProcessBatchAnalysisAsync(string? repo) => Task.CompletedTask;
        public Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request)
            => Task.FromResult(new DiscoverResponse(0, 0, 0, 0, 0, []));
    }

    private sealed class FakeIndexerRunStore : IIndexerRunStore
    {
        private readonly Dictionary<long, IndexerRunEntity> _runs = new();
        private long _nextId = 1;

        public Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default)
        {
            run.Id = _nextId++;
            _runs[run.Id] = Clone(run);
            return Task.FromResult(run.Id);
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

        private static IndexerRunEntity Clone(IndexerRunEntity run) => new()
        {
            Id = run.Id,
            Operation = run.Operation,
            Status = run.Status,
            RequestedByUsername = run.RequestedByUsername,
            Target = run.Target,
            ArgsJson = run.ArgsJson,
            Message = run.Message,
            Error = run.Error,
            CreatedAt = run.CreatedAt,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt
        };
    }
}
