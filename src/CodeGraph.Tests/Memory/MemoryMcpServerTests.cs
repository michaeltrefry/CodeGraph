using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Work;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryMcpServerTests
{
    [Fact]
    public async Task StoreMemoryV2_AcceptsTypedArgumentsAndReturnsStructuredAck()
    {
        var bus = new FakeMessageBus();
        var memoryOperations = CreateMemoryOperations(bus);

        var response = await MemoryMcpServer.StoreMemoryV2(
            memoryOperations,
            NullLogger<MemoryMcpServer>.Instance,
            source: "thread-123",
            claims:
            [
                new MemoryMcpClaimInput
                {
                    Subject = "michael",
                    Predicate = "prefers",
                    ValueText = "clean slate design",
                }
            ]);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("status").GetString().ShouldBe("queued");
        document.RootElement.GetProperty("receiptId").GetString().ShouldNotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("inputMode").GetString().ShouldBe("typed");
        document.RootElement.GetProperty("claimsRequested").GetInt32().ShouldBe(1);

        bus.Published.ShouldHaveSingleItem();
        bus.Published[0].ReceiptId.ShouldNotBeNullOrWhiteSpace();
        bus.Published[0].Username.ShouldBe("user:test");
        bus.Published[0].Source.ShouldBe("thread-123");
        bus.Published[0].Extraction.Claims.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StoreMemoryV2_RejectsMixedTypedAndJsonInput()
    {
        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(new FakeMessageBus()),
            NullLogger<MemoryMcpServer>.Instance,
            data: """{"claims":[{"subject":"michael","predicate":"prefers","valueText":"clean slate design"}]}""",
            claims:
            [
                new MemoryMcpClaimInput
                {
                    Subject = "michael",
                    Predicate = "prefers",
                    ValueText = "clean slate design",
                }
            ]);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("Provide either typed arguments or legacy JSON data, not both.");
    }

    [Fact]
    public async Task StoreMemoryV2_ReturnsStructuredValidationErrorForMissingTypedFields()
    {
        var bus = new FakeMessageBus();
        var entities = JsonSerializer.Deserialize<List<MemoryMcpEntityInput>>("""[{"id":"michael","type":"person"}]""");

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            entities: entities);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_input");
        document.RootElement.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("entities[0].label is required.");
        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreMemoryV2_AIFunctionBoundaryDoesNotThrowWhenEntityLabelIsMissing()
    {
        var bus = new FakeMessageBus();
        var function = AIFunctionFactory.Create(
            async (List<MemoryMcpEntityInput>? entities) => await MemoryMcpServer.StoreMemoryV2(
                CreateMemoryOperations(bus),
                NullLogger<MemoryMcpServer>.Instance,
                entities: entities));
        using var argumentsJson = JsonDocument.Parse("""[{"id":"michael","type":"person"}]""");

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["entities"] = argumentsJson.RootElement.Clone(),
        });

        var response = result.ShouldBeOfType<JsonElement>().GetString();
        using var document = JsonDocument.Parse(response.ShouldNotBeNull());
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_input");
        document.RootElement.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("entities[0].label is required.");
        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreMemoryV2_ReportsAllRequiredTypedFieldsWithoutDeserializationExceptions()
    {
        var bus = new FakeMessageBus();
        var entities = JsonSerializer.Deserialize<List<MemoryMcpEntityInput>>("""[{}]""");
        var claims = JsonSerializer.Deserialize<List<MemoryMcpClaimInput>>("""[{}]""");
        var evidence = JsonSerializer.Deserialize<List<MemoryMcpEvidenceInput>>("""[{}]""");

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            entities: entities,
            claims: claims,
            evidence: evidence);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe(
            "entities[0].id is required. entities[0].label is required. entities[0].type is required. " +
            "claims[0].subject is required. claims[0].predicate is required. " +
            "evidence[0].evidenceType is required. evidence[0].sourceRef is required.");
        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreMemoryV2_ReturnsStructuredValidationErrorForLegacyJsonMissingLabel()
    {
        var bus = new FakeMessageBus();

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            data: """{"entities":[{"id":"michael","type":"person"}]}""");

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_json");
        (document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty)
            .ShouldContain("missing required properties including: 'label'");
        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreMemoryV2_ScopesTheWriteToTheSessionsProject()
    {
        var bus = new FakeMessageBus();
        var resolver = new FakeProjectResolver(sessions: new() { ["session-1"] = "CG" });

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            claims: [new MemoryMcpClaimInput { Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
            sessionId: "session-1",
            projectResolver: resolver);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("project").GetString().ShouldBe("CG");
        bus.Published.ShouldHaveSingleItem().ProjectKey.ShouldBe("CG");
    }

    [Fact]
    public async Task StoreMemoryV2_WithoutProjectOrSessionPassesTheCallerToTheResolver()
    {
        var bus = new FakeMessageBus();
        var resolver = new FakeProjectResolver(liveSessions: new() { ["alice"] = "CG" });
        var alice = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "McpPat"));

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            claims: [new MemoryMcpClaimInput { Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
            projectResolver: resolver,
            user: alice);

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("project").GetString().ShouldBe("CG");
        bus.Published.ShouldHaveSingleItem().ProjectKey.ShouldBe("CG");
        resolver.Callers.ShouldHaveSingleItem()!.Name.ShouldBe("alice");
    }

    [Fact]
    public async Task StoreTools_OverMcp_ResolveTheProjectForTheRequestsUser()
    {
        var bus = new FakeMessageBus();
        var resolver = new FakeProjectResolver(liveSessions: new() { ["alice"] = "CG" });
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddSingleton(CreateMemoryOperations(bus));
                    services.AddSingleton<IMemoryProjectResolver>(resolver);
                    services.AddMcpServer().WithHttpTransport().WithTools<MemoryMcpServer>().WithTools<ConsolidatedMcpServer>();
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "McpPat"));
                        await next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }))
            .StartAsync();
        var http = host.GetTestClient();
        string? session = null;

        async Task<string> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (session is not null)
                request.Headers.Add("Mcp-Session-Id", session);
            using var response = await http.SendAsync(request);
            session ??= response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null;
            return await response.Content.ReadAsStringAsync();
        }

        await PostAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        await PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var id = 1;
        foreach (var tool in new[] { "store_memory_v2", "memory_store" })
        {
            var body = await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool +
                "\",\"arguments\":{\"claims\":[{\"subject\":\"codegraph\",\"predicate\":\"uses\",\"object\":\"mariadb\"}]}}}");
            body.ShouldNotContain("\"isError\":true");
        }

        resolver.Callers.Select(caller => caller?.Name).ShouldBe(["alice", "alice"]);
        bus.Published.Select(message => message.ProjectKey).ShouldBe(["CG", "CG"]);
    }

    [Theory]
    [InlineData(typeof(MemoryMcpServer), nameof(MemoryMcpServer.StoreMemoryV2))]
    [InlineData(typeof(ConsolidatedMcpServer), nameof(ConsolidatedMcpServer.MemoryStore))]
    public void StoreTools_AdvertiseProjectAndSessionIdAndTakeTheResolverFromServices(Type type, string method)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMemoryOperationsService>(CreateMemoryOperations(new FakeMessageBus()))
            .AddSingleton<IMemoryProjectResolver>(new FakeProjectResolver())
            .BuildServiceProvider();

        var tool = McpServerTool.Create(type.GetMethod(method)!, target: null, new McpServerToolCreateOptions { Services = services });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        properties.TryGetProperty("project", out _).ShouldBeTrue();
        properties.TryGetProperty("sessionId", out _).ShouldBeTrue();
        properties.TryGetProperty("projectResolver", out _).ShouldBeFalse();
        properties.TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StoreMemoryV2_WithoutProjectOrSessionStaysUnscoped()
    {
        var bus = new FakeMessageBus();

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            claims: [new MemoryMcpClaimInput { Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
            projectResolver: new FakeProjectResolver());

        using var document = JsonDocument.Parse(response);
        document.RootElement.TryGetProperty("project", out _).ShouldBeFalse();
        bus.Published.ShouldHaveSingleItem().ProjectKey.ShouldBeNull();
    }

    [Fact]
    public async Task StoreMemoryV2_RefusesAnUnknownProjectWithoutQueueing()
    {
        var bus = new FakeMessageBus();

        var response = await MemoryMcpServer.StoreMemoryV2(
            CreateMemoryOperations(bus),
            NullLogger<MemoryMcpServer>.Instance,
            claims: [new MemoryMcpClaimInput { Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
            project: "NOPE",
            projectResolver: new FakeProjectResolver());

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_project");
        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreClaims_WithAProjectScopesEveryWrittenEntityAndClaimAndEachEvidencedClaim()
    {
        var store = new FakeMemoryGraphStore();
        var memoryService = CreateMemoryService(store);
        var extraction = new MemoryClaimExtractionResult
        {
            Entities = [new MemoryExtractedEntity { Id = "codegraph", Label = "CodeGraph", Type = "repository" }],
            Claims = [new MemoryExtractedClaim { Id = "claim_uses", Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
            Evidence =
            [
                new MemoryExtractedEvidence { ClaimId = "claim_uses", EvidenceType = "note", SourceRef = "CodeGraph#163" },
                new MemoryExtractedEvidence { ClaimId = "claim_earlier", EvidenceType = "note", SourceRef = "CodeGraph#164" },
            ],
        };

        await memoryService.StoreClaimsAsync(extraction, "test", "CG");

        var scope = store.ProjectScopes.ShouldHaveSingleItem();
        scope.ProjectKey.ShouldBe("CG");
        scope.EntityIds.ShouldBe(["codegraph", "mariadb"], ignoreOrder: true);
        scope.ClaimIds.ShouldBe(["claim_uses", "claim_earlier"], ignoreOrder: true);

        await memoryService.StoreClaimsAsync(extraction, "test");
        store.ProjectScopes.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetMemoryWriteStatus_ReturnsStructuredReceipt()
    {
        var store = new FakeMemoryGraphStore();
        var memoryService = CreateMemoryService(store);

        await store.CreateWriteReceiptAsync(new MemoryWriteReceipt
        {
            Id = "memory_write_123",
            Source = "thread-123",
            InputMode = "typed",
            Status = MemoryWriteReceiptStatus.Completed,
            ClaimsRequested = 1,
            ClaimsWritten = 1,
        });

        var response = await MemoryMcpServer.GetMemoryWriteStatus(
            "memory_write_123",
            new LocalMemoryOperationsService(memoryService, new FakeMessageBus()));

        using var document = JsonDocument.Parse(response);
        document.RootElement.GetProperty("id").GetString().ShouldBe("memory_write_123");
        document.RootElement.GetProperty("status").GetString().ShouldBe("Completed");
        document.RootElement.GetProperty("claimsWritten").GetInt32().ShouldBe(1);
    }

    private static MemoryService CreateMemoryService(FakeMemoryGraphStore? store = null)
    {
        store ??= new FakeMemoryGraphStore();
        var ingestion = new MemoryClaimIngestionService(store, new FakeEmbeddingService(),
            NullLogger<MemoryClaimIngestionService>.Instance);
        var retrieval = new MemoryRetrievalService(store, new FakeEmbeddingService(),
            NullLogger<MemoryRetrievalService>.Instance);

        return new MemoryService(
            ingestion,
            new MemoryLegacyMigrationService(store, ingestion, NullLogger<MemoryLegacyMigrationService>.Instance),
            new MemoryObservationMigrationService(store, NullLogger<MemoryObservationMigrationService>.Instance),
            retrieval,
            store,
            NullLogger<MemoryService>.Instance);
    }

    private static IMemoryOperationsService CreateMemoryOperations(FakeMessageBus bus, FakeMemoryGraphStore? store = null)
        => new LocalMemoryOperationsService(CreateMemoryService(store), bus);

    private sealed class FakeMessageBus : IMessageBus
    {
        public List<StoreMemoryClaims> Published { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            if (message is StoreMemoryClaims storeMemoryClaims)
                Published.Add(storeMemoryClaims);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectResolver(Dictionary<string, string>? sessions = null, Dictionary<string, string>? liveSessions = null) : IMemoryProjectResolver
    {
        public List<WorkActor?> Callers { get; } = [];

        public Task<string?> ResolveAsync(string? project, string? sessionId, WorkActor? caller = null, CancellationToken ct = default)
        {
            Callers.Add(caller);
            if (project is not null)
                throw new MemoryProjectResolutionException($"project '{project}' is not a project.");
            if (sessionId is null)
                return Task.FromResult(caller is not null && liveSessions is not null && liveSessions.TryGetValue(caller.Name, out var live) ? live : null);
            return sessions is not null && sessions.TryGetValue(sessionId, out var key)
                ? Task.FromResult<string?>(key)
                : throw new MemoryProjectResolutionException($"sessionId '{sessionId}' is not a session.");
        }
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool IsAvailable => false;
        public string ModelName => "test";
        public int Dimensions => 0;
        public float[] GenerateEmbedding(string text) => [];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => [];
    }

    private sealed class FakeMemoryGraphStore : IMemoryGraphStore
    {
        public string Username => "user:test";
        public Dictionary<string, MemoryWriteReceipt> WriteReceipts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task CreateWriteReceiptAsync(MemoryWriteReceipt receipt)
        {
            WriteReceipts[receipt.Id] = receipt;
            return Task.CompletedTask;
        }

        public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId) =>
            Task.FromResult(WriteReceipts.GetValueOrDefault(receiptId));

        public Task UpdateWriteReceiptStatusAsync(string receiptId, MemoryWriteReceiptStatus status, StoreMemoryResult? result = null,
            string? errorMessage = null)
        {
            if (WriteReceipts.TryGetValue(receiptId, out var receipt))
            {
                receipt.Status = status;
                receipt.ErrorMessage = errorMessage;
                if (result != null)
                {
                    receipt.NodesWritten = result.NodesWritten;
                    receipt.EdgesWritten = result.EdgesWritten;
                    receipt.ClaimsWritten = result.ClaimsWritten;
                    receipt.EvidenceWritten = result.EvidenceWritten;
                    receipt.ObservationsWritten = result.ObservationsWritten;
                    receipt.ConflictsDetected = result.ConflictsDetected;
                }
            }

            return Task.CompletedTask;
        }

        public Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities) => Task.CompletedTask;
        public Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims) => Task.CompletedTask;
        public Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges) => Task.CompletedTask;
        public Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence) => Task.CompletedTask;
        public Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges) => Task.CompletedTask;
        public Task CreateObservationAsync(MemoryObservation obs) => Task.CompletedTask;
        public Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId) => Task.CompletedTask;
        public Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5, string? projectKey = null) =>
            Task.FromResult(new List<(MemoryEntity Entity, double Score)>());
        public Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5, string? projectKey = null) => Task.FromResult(new List<MemoryEntity>());
        public Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId) => Task.FromResult(new List<MemoryRelationshipDetail>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds) =>
            Task.FromResult(new List<MemoryObservation>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds) =>
            Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0, string? projectKey = null) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200, string? projectKey = null) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20) => Task.FromResult(new List<string>());
        public Task<MemoryEntity?> GetEntityAsync(string entityId) => Task.FromResult<MemoryEntity?>(null);
        public Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId) => Task.FromResult<MemoryEntity?>(null);
        public Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync() => Task.FromResult(new List<MemoryLegacyRelationship>());
        public Task<List<MemoryObservation>> GetAllObservationsAsync() => Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryClaim?> GetClaimAsync(string claimId) => Task.FromResult<MemoryClaim?>(null);
        public Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey) => Task.FromResult(new List<MemoryClaim>());
        public Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate) => Task.FromResult(new List<MemoryClaim>());
        public Task<MemoryEntityBundle?> GetEntityBundleAsync(string entityId, bool includeSuperseded = false, bool includeConflicts = true, int neighborLimit = 20, string? projectKey = null)
            => Task.FromResult<MemoryEntityBundle?>(null);
        public Task<MemoryClaimBundle?> GetClaimBundleAsync(string claimId, bool includeSupersessionChain = true, bool includeConflicts = true, bool includeEvidence = true, string? projectKey = null)
            => Task.FromResult<MemoryClaimBundle?>(null);
        public Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(string query, float[]? queryEmbedding, int limit = 5, bool includeSuperseded = false, string? projectKey = null)
            => Task.FromResult(new List<(MemoryClaim Claim, double Score, string MatchKind)>());
        public List<(string ProjectKey, IReadOnlyCollection<string> EntityIds, IReadOnlyCollection<string> ClaimIds)> ProjectScopes { get; } = [];
        public Task AddProjectScopeAsync(string projectKey, IReadOnlyCollection<string> entityIds, IReadOnlyCollection<string> claimIds)
        {
            ProjectScopes.Add((projectKey, entityIds, claimIds));
            return Task.CompletedTask;
        }
        public Task<bool> IsEntityInProjectAsync(string entityId, string projectKey) => Task.FromResult(false);
        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphQuery query, int maxHops = 2, int maxReturnedEntities = 20,
            int maxReturnedClaims = 40, bool includeSuperseded = false, bool includeConflicts = true)
            => Task.FromResult(new MemorySubgraphResult());
    }
}
