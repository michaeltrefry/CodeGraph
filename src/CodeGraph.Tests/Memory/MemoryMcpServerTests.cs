using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Logging.Abstractions;
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
                new MemoryExtractedClaim
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
                new MemoryExtractedClaim
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
        public Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5) =>
            Task.FromResult(new List<(MemoryEntity Entity, double Score)>());
        public Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5) => Task.FromResult(new List<MemoryEntity>());
        public Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId) => Task.FromResult(new List<MemoryRelationshipDetail>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds) =>
            Task.FromResult(new List<MemoryObservation>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds) =>
            Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20) => Task.FromResult(new List<string>());
        public Task<MemoryEntity?> GetEntityAsync(string entityId) => Task.FromResult<MemoryEntity?>(null);
        public Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId) => Task.FromResult<MemoryEntity?>(null);
        public Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync() => Task.FromResult(new List<MemoryLegacyRelationship>());
        public Task<List<MemoryObservation>> GetAllObservationsAsync() => Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryClaim?> GetClaimAsync(string claimId) => Task.FromResult<MemoryClaim?>(null);
        public Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey) => Task.FromResult(new List<MemoryClaim>());
        public Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate) => Task.FromResult(new List<MemoryClaim>());
        public Task<MemoryEntityBundle?> GetEntityBundleAsync(string entityId, bool includeSuperseded = false, bool includeConflicts = true, int neighborLimit = 20)
            => Task.FromResult<MemoryEntityBundle?>(null);
        public Task<MemoryClaimBundle?> GetClaimBundleAsync(string claimId, bool includeSupersessionChain = true, bool includeConflicts = true, bool includeEvidence = true)
            => Task.FromResult<MemoryClaimBundle?>(null);
        public Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(string query, float[]? queryEmbedding, int limit = 5, bool includeSuperseded = false)
            => Task.FromResult(new List<(MemoryClaim Claim, double Score, string MatchKind)>());
        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphQuery query, int maxHops = 2, int maxReturnedEntities = 20,
            int maxReturnedClaims = 40, bool includeSuperseded = false, bool includeConflicts = true)
            => Task.FromResult(new MemorySubgraphResult());
    }
}
