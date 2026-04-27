using CodeGraph.Api.Controllers;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class MemoryControllerCleanupTests
{
    [Fact]
    public async Task DeleteBySource_RejectsBlankSource()
    {
        var controller = CreateController(new RecordingMemoryOperations());

        var result = await controller.DeleteBySource(new MemoryCleanupBySourceRequest { Source = " " });

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteBySource_ForwardsDryRunRequest()
    {
        var operations = new RecordingMemoryOperations();
        var controller = CreateController(operations);

        var result = await controller.DeleteBySource(new MemoryCleanupBySourceRequest
        {
            Source = "test",
            DryRun = true,
        });

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<MemoryCleanupResult>().Scope.ShouldBe("source");
        operations.SourceRequest.ShouldBe(("test", true));
    }

    [Fact]
    public async Task DeleteByIds_RequiresAtLeastOneId()
    {
        var controller = CreateController(new RecordingMemoryOperations());

        var result = await controller.DeleteByIds(new MemoryCleanupByIdsRequest());

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteByIds_ForwardsClaimAndEntityIds()
    {
        var operations = new RecordingMemoryOperations();
        var controller = CreateController(operations);

        var result = await controller.DeleteByIds(new MemoryCleanupByIdsRequest
        {
            ClaimIds = ["claim_1"],
            EntityIds = ["entity_1"],
            DryRun = false,
        });

        result.Result.ShouldBeOfType<OkObjectResult>();
        operations.IdsRequest.ShouldNotBeNull();
        operations.IdsRequest.Value.ClaimIds.ShouldBe(["claim_1"]);
        operations.IdsRequest.Value.EntityIds.ShouldBe(["entity_1"]);
        operations.IdsRequest.Value.DryRun.ShouldBeFalse();
    }

    private static MemoryController CreateController(IMemoryOperationsService operations)
    {
        return new MemoryController(operations)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            }
        };
    }

    private sealed class RecordingMemoryOperations : IMemoryOperationsService
    {
        public (string Source, bool DryRun)? SourceRequest { get; private set; }
        public (IReadOnlyList<string> ClaimIds, IReadOnlyList<string> EntityIds, bool DryRun)? IdsRequest { get; private set; }

        public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
            string source,
            bool dryRun,
            CancellationToken ct = default)
        {
            SourceRequest = (source, dryRun);
            return Task.FromResult(new MemoryCleanupResult
            {
                Scope = "source",
                DryRun = dryRun,
                Sources = [source],
            });
        }

        public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(bool dryRun, CancellationToken ct = default)
        {
            return Task.FromResult(new MemoryCleanupResult
            {
                Scope = "test_data",
                DryRun = dryRun,
            });
        }

        public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
            IReadOnlyList<string> claimIds,
            IReadOnlyList<string> entityIds,
            bool dryRun,
            CancellationToken ct = default)
        {
            IdsRequest = (claimIds, entityIds, dryRun);
            return Task.FromResult(new MemoryCleanupResult
            {
                Scope = "explicit_items",
                DryRun = dryRun,
                ClaimIds = claimIds,
                EntityIds = entityIds,
            });
        }

        public Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
            MemoryClaimExtractionResult extraction,
            string source,
            string inputMode,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
            int staleAfterMinutes = 15,
            int sampleLimit = 10,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
            int staleAfterMinutes = 15,
            int sampleLimit = 10,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySearchResult> SearchMemoryAsync(
            string query,
            int entityLimit = 5,
            int claimLimit = 5,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryEntityBundle?> GetEntityBundleAsync(
            string entityId,
            bool includeSuperseded = false,
            bool includeConflicts = true,
            int neighborLimit = 20,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryGraphSnapshot> GetEntityGraphAsync(
            string entityId,
            int neighborLimit = 200,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryClaimBundle?> GetClaimBundleAsync(
            string claimId,
            bool includeSupersessionChain = true,
            bool includeConflicts = true,
            bool includeEvidence = true,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(
            MemoryFrontierExpansionRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(
            MemorySummaryRenderRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryQueryResult> QueryAsync(
            string topic,
            int hops = 2,
            int maxNodes = 20,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryEntityWithRelationships?> GetEntityWithRelationshipsAsync(
            string entityId,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
