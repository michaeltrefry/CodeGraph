using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using System.Security.Claims;

namespace CodeGraph.Tests.Controllers;

public class DatabaseSourcesControllerTests
{
    [Fact]
    public async Task List_ReturnsMaskedConnectionStrings()
    {
        var store = new RecordingDatabaseSourceStore();
        await store.CreateAsync(new DatabaseSourceEntity
        {
            ServerName = "mysql",
            DatabaseName = "codegraph",
            ConnectionString = "Server=mysql;Database=codegraph;User ID=codegraph;Password=secret;",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var controller = new DatabaseSourcesController(store, new RecordingIndexerOperationsService());

        var result = await controller.List();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var sources = ok.Value.ShouldBeAssignableTo<IReadOnlyList<DatabaseSourceResponse>>();
        sources.ShouldNotBeNull();
        sources.Single().ConnectionString.ShouldContain("Password=***");
        sources.Single().ConnectionString.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Create_ValidatesAndPersistsSource()
    {
        var store = new RecordingDatabaseSourceStore();
        var controller = new DatabaseSourcesController(store, new RecordingIndexerOperationsService());

        var result = await controller.Create(new CreateDatabaseSourceRequest(
            " mysql ",
            " codegraph ",
            " Server=mysql;Database=codegraph;Pwd=secret; ",
            Enabled: null));

        var createdAt = result.Result.ShouldBeOfType<CreatedAtActionResult>();
        var source = createdAt.Value.ShouldBeOfType<DatabaseSourceResponse>();
        source.ServerName.ShouldBe("mysql");
        source.DatabaseName.ShouldBe("codegraph");
        source.Enabled.ShouldBeTrue();
        source.ConnectionString.ShouldContain("Pwd=***");
        (await store.GetAsync(source.Id))!.ConnectionString.ShouldContain("secret");
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenSourceDoesNotExist()
    {
        var controller = new DatabaseSourcesController(new RecordingDatabaseSourceStore(), new RecordingIndexerOperationsService());

        var result = await controller.Update(404, new UpdateDatabaseSourceRequest("mysql", null, null, true));

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_RemovesStoredSource()
    {
        var store = new RecordingDatabaseSourceStore();
        var created = await store.CreateAsync(new DatabaseSourceEntity
        {
            ServerName = "mysql",
            ConnectionString = "Server=mysql;Pwd=secret;",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var controller = new DatabaseSourcesController(store, new RecordingIndexerOperationsService());

        var result = await controller.Delete(created.Id);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(created.Id)).ShouldBeNull();
    }

    [Fact]
    public void GenerateKey_ReturnsBase64AesKey()
    {
        var controller = new DatabaseSourcesController(new RecordingDatabaseSourceStore(), new RecordingIndexerOperationsService());

        var result = controller.GenerateKey();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var key = GetValue<string>(ok.Value!, "key");
        Convert.FromBase64String(key).Length.ShouldBe(32);
    }

    [Fact]
    public async Task Sync_QueuesIndexerRunForSource()
    {
        var store = new RecordingDatabaseSourceStore();
        var source = await store.CreateAsync(new DatabaseSourceEntity
        {
            ServerName = "mysql",
            ConnectionString = "Server=mysql;Pwd=secret;",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var indexer = new RecordingIndexerOperationsService();
        var controller = CreateController(store, indexer);

        var result = await controller.Sync(source.Id, CancellationToken.None);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        var payload = accepted.Value.ShouldBeOfType<IndexerAcceptedResponse>();
        payload.RunStatusUrl.ShouldBe("/api/indexer/runs/55");
        indexer.LastUsername.ShouldBe("Michael");
        indexer.LastSourceId.ShouldBe(source.Id);
    }

    [Fact]
    public async Task Sync_ReturnsNotFound_WhenSourceMissing()
    {
        var controller = CreateController(new RecordingDatabaseSourceStore(), new RecordingIndexerOperationsService());

        var result = await controller.Sync(404, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    private static T GetValue<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        property.ShouldNotBeNull();
        return (T)property.GetValue(value)!;
    }

    private static DatabaseSourcesController CreateController(
        RecordingDatabaseSourceStore store,
        RecordingIndexerOperationsService indexer)
    {
        return new DatabaseSourcesController(store, indexer)
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
        public string? LastUsername { get; private set; }
        public long? LastSourceId { get; private set; }

        public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        {
            LastUsername = username;
            LastSourceId = sourceId;
            return Task.FromResult(new IndexerAcceptedResponse("queued", "Queued.", 55, "/api/indexer/runs/55"));
        }

        public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        {
            LastUsername = username;
            return Task.FromResult(new IndexerAcceptedResponse("queued", "Queued.", 56, "/api/indexer/runs/56"));
        }

        public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
            => Task.FromResult<IndexerRunResponse?>(null);

        public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
            string? status = null,
            string? operation = null,
            int take = 50,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IndexerRunResponse>>([]);
    }

    private sealed class RecordingDatabaseSourceStore : IDatabaseSourceStore
    {
        private readonly List<DatabaseSourceEntity> _sources = [];
        private long _nextId = 1;

        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() =>
            Task.FromResult<IReadOnlyList<DatabaseSourceEntity>>(_sources.Select(Clone).ToList());

        public Task<DatabaseSourceEntity?> GetAsync(long id) =>
            Task.FromResult(_sources.Where(s => s.Id == id).Select(Clone).FirstOrDefault());

        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity)
        {
            var clone = Clone(entity);
            clone.Id = _nextId++;
            _sources.Add(clone);
            return Task.FromResult(Clone(clone));
        }

        public Task<DatabaseSourceEntity?> UpdateAsync(
            long id,
            string? serverName,
            string? databaseName,
            string? connectionString,
            bool? enabled)
        {
            var existing = _sources.FirstOrDefault(s => s.Id == id);
            if (existing is null)
            {
                return Task.FromResult<DatabaseSourceEntity?>(null);
            }

            if (serverName is not null)
                existing.ServerName = serverName;
            if (databaseName is not null)
                existing.DatabaseName = databaseName;
            if (connectionString is not null)
                existing.ConnectionString = connectionString;
            if (enabled is not null)
                existing.Enabled = enabled.Value;
            existing.UpdatedAt = DateTime.UtcNow;

            return Task.FromResult<DatabaseSourceEntity?>(Clone(existing));
        }

        public Task<bool> DeleteAsync(long id)
        {
            var removed = _sources.RemoveAll(s => s.Id == id) > 0;
            return Task.FromResult(removed);
        }

        public Task UpdateLastSyncedAsync(long id)
        {
            var existing = _sources.FirstOrDefault(s => s.Id == id);
            if (existing is not null)
            {
                existing.LastSyncedAt = DateTime.UtcNow;
                existing.UpdatedAt = existing.LastSyncedAt.Value;
            }

            return Task.CompletedTask;
        }

        private static DatabaseSourceEntity Clone(DatabaseSourceEntity entity) => new()
        {
            Id = entity.Id,
            ServerName = entity.ServerName,
            DatabaseName = entity.DatabaseName,
            ConnectionString = entity.ConnectionString,
            Enabled = entity.Enabled,
            LastSyncedAt = entity.LastSyncedAt,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
}
