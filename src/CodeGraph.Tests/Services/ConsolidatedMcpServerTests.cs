using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Memory;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ConsolidatedMcpServerTests
{
    // ---- Envelope shape -------------------------------------------------------------------

    [Fact]
    public async Task Envelope_CarriesToolOperationAndFormatVersion()
    {
        var codegraph = CreateCodeGraphServer(new InMemoryGraphStore());

        var response = await ConsolidatedMcpServer.CodegraphSearch(codegraph, operation: "schema");

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("tool").GetString().ShouldBe("codegraph_search");
        doc.RootElement.GetProperty("operation").GetString().ShouldBe("schema");
        doc.RootElement.GetProperty("formatVersion").GetString().ShouldBe(ConsolidatedMcpServer.FormatVersion);
        // Markdown legacy output is embedded verbatim as a string, kept close to legacy.
        doc.RootElement.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.String);
        doc.RootElement.GetProperty("result").GetString().ShouldContain("Node Types");
    }

    [Fact]
    public async Task Envelope_EmbedsJsonLegacyOutputAsStructuredNode()
    {
        var memory = new RecordingMemoryOperationsService();

        var response = await ConsolidatedMcpServer.MemoryDiagnostics(
            memory, operation: "write_status", receiptId: "memory_write_1");

        using var doc = JsonDocument.Parse(response);
        // The legacy GetMemoryWriteStatus output is JSON, so it is embedded as an object.
        var result = doc.RootElement.GetProperty("result");
        result.ValueKind.ShouldBe(JsonValueKind.Object);
        result.GetProperty("id").GetString().ShouldBe("memory_write_1");
    }

    // ---- CodeGraph routing ---------------------------------------------------------------

    [Fact]
    public async Task CodegraphSearch_Schema_RoutesToGetGraphSchema()
    {
        var codegraph = CreateCodeGraphServer(new InMemoryGraphStore());

        var response = await ConsolidatedMcpServer.CodegraphSearch(codegraph, operation: "schema");

        Result(response).ShouldBe(codegraph.GetGraphSchema());
    }

    [Fact]
    public async Task ProjectReport_Search_RoutesToSearchProjects()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "CodeGraph",
            RepoUrl = "https://github.com/example/codegraph.git",
        });
        var codegraph = CreateCodeGraphServer(store);

        var response = await ConsolidatedMcpServer.ProjectReport(codegraph, operation: "search", search: "Code");

        Result(response).ShouldContain("CodeGraph");
        Result(response).ShouldContain("## Indexed Projects (1)");
    }

    [Fact]
    public async Task RagSearch_RoutesToSearchConventions()
    {
        var codegraph = CreateCodeGraphServer(new InMemoryGraphStore());

        var response = await ConsolidatedMcpServer.RagSearch(codegraph, operation: "search", query: "gateway");

        // No convention embedding service is configured in this fixture, so the legacy
        // SearchConventionsAsync path returns its not-configured message — proving routing.
        Result(response).ShouldBe("Convention semantic search is not configured.");
    }

    // ---- Memory routing ------------------------------------------------------------------

    [Theory]
    [InlineData("query", "QueryAsync")]
    [InlineData("search", "SearchMemoryAsync")]
    [InlineData("subgraph", "GetMemorySubgraphAsync")]
    [InlineData("entity_bundle", "GetEntityBundleAsync")]
    [InlineData("claim_bundle", "GetClaimBundleAsync")]
    [InlineData("expand_frontier", "ExpandMemoryFrontierAsync")]
    [InlineData("render_summary", "RenderMemorySummaryAsync")]
    public async Task MemoryRead_RoutesEachOperationToTheIntendedService(string operation, string expectedCall)
    {
        var memory = new RecordingMemoryOperationsService();

        var response = await ConsolidatedMcpServer.MemoryRead(
            memory,
            operation,
            query: "topic",
            entityId: "entity-1",
            claimId: "claim-1",
            entityIds: "e1,e2",
            claimIds: "c1");

        memory.Calls.ShouldHaveSingleItem();
        memory.Calls[0].ShouldBe(expectedCall);
        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("operation").GetString().ShouldBe(operation);
    }

    [Fact]
    public async Task MemoryRead_PreservesLegacyPerOperationLimitDefaults()
    {
        var memory = new RecordingMemoryOperationsService();

        await ConsolidatedMcpServer.MemoryRead(memory, "search", query: "topic");

        // search's legacy defaults are 5/5, not memory_read's shared fallback.
        memory.LastEntityLimit.ShouldBe(5);
        memory.LastClaimLimit.ShouldBe(5);
    }

    [Fact]
    public async Task MemoryDiagnostics_RoutesToGetWriteReceipt()
    {
        var memory = new RecordingMemoryOperationsService();

        await ConsolidatedMcpServer.MemoryDiagnostics(memory, "write_status", receiptId: "memory_write_1");

        memory.Calls.ShouldHaveSingleItem();
        memory.Calls[0].ShouldBe("GetWriteReceiptAsync");
    }

    [Fact]
    public async Task MemoryStore_RoutesToQueueClaims()
    {
        var memory = new RecordingMemoryOperationsService();

        var response = await ConsolidatedMcpServer.MemoryStore(
            memory,
            NullLogger<MemoryMcpServer>.Instance,
            operation: "store",
            claims: [new MemoryExtractedClaim { Subject = "michael", Predicate = "prefers", ValueText = "x" }]);

        memory.Calls.ShouldHaveSingleItem();
        memory.Calls[0].ShouldBe("QueueClaimsAsync");
        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("operation").GetString().ShouldBe("store");
    }

    // ---- Invalid operations --------------------------------------------------------------

    [Theory]
    [InlineData("codegraph_search")]
    [InlineData("rag_search")]
    [InlineData("graph_trace")]
    [InlineData("graph_source")]
    [InlineData("project_report")]
    [InlineData("graph_cluster")]
    [InlineData("storage_table")]
    [InlineData("convention")]
    [InlineData("memory_read")]
    [InlineData("memory_store")]
    [InlineData("memory_diagnostics")]
    public async Task EveryTool_RejectsUnknownOperation(string tool)
    {
        var response = await InvokeAsync(tool, "definitely_not_a_real_operation");

        AssertError(response, tool, "invalid_operation");
    }

    // ---- Missing required arguments ------------------------------------------------------

    [Theory]
    [InlineData("codegraph_search", "search")]
    [InlineData("rag_search", "search")]
    [InlineData("graph_trace", "call_path")]
    [InlineData("graph_trace", "data_lineage")]
    [InlineData("graph_trace", "consumers")]
    [InlineData("graph_trace", "publishers")]
    [InlineData("graph_trace", "impact")]
    [InlineData("graph_source", "snippet")]
    [InlineData("graph_source", "node")]
    [InlineData("project_report", "summary")]
    [InlineData("project_report", "architecture")]
    [InlineData("project_report", "health")]
    [InlineData("graph_cluster", "detail")]
    [InlineData("storage_table", "catalog")]
    [InlineData("convention", "get")]
    [InlineData("memory_read", "query")]
    [InlineData("memory_read", "search")]
    [InlineData("memory_read", "entity_bundle")]
    [InlineData("memory_read", "claim_bundle")]
    [InlineData("memory_diagnostics", "write_status")]
    public async Task Operation_RejectsMissingRequiredArgument(string tool, string operation)
    {
        var response = await InvokeAsync(tool, operation);

        AssertError(response, tool, "missing_argument");
        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("operation").GetString().ShouldBe(operation);
    }

    // ---- Helpers -------------------------------------------------------------------------

    // Invokes a consolidated tool by name with only an operation supplied and all other
    // arguments omitted, so it exercises the operation switch and argument validation
    // without needing working downstream dependencies.
    private static async Task<string> InvokeAsync(string tool, string operation)
    {
        var codegraph = CreateCodeGraphServer(new InMemoryGraphStore());
        var memory = new RecordingMemoryOperationsService();
        return tool switch
        {
            "codegraph_search" => await ConsolidatedMcpServer.CodegraphSearch(codegraph, operation),
            "rag_search" => await ConsolidatedMcpServer.RagSearch(codegraph, operation),
            "graph_trace" => await ConsolidatedMcpServer.GraphTrace(codegraph, operation),
            "graph_source" => await ConsolidatedMcpServer.GraphSource(codegraph, operation),
            "project_report" => await ConsolidatedMcpServer.ProjectReport(codegraph, operation),
            "graph_cluster" => await ConsolidatedMcpServer.GraphCluster(codegraph, operation),
            "storage_table" => await ConsolidatedMcpServer.StorageTable(codegraph, operation),
            "convention" => await ConsolidatedMcpServer.Convention(codegraph, operation),
            "memory_read" => await ConsolidatedMcpServer.MemoryRead(memory, operation),
            "memory_store" => await ConsolidatedMcpServer.MemoryStore(memory, NullLogger<MemoryMcpServer>.Instance, operation),
            "memory_diagnostics" => await ConsolidatedMcpServer.MemoryDiagnostics(memory, operation),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool"),
        };
    }

    private static void AssertError(string response, string expectedTool, string expectedCode)
    {
        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("tool").GetString().ShouldBe(expectedTool);
        doc.RootElement.GetProperty("formatVersion").GetString().ShouldBe(ConsolidatedMcpServer.FormatVersion);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(expectedCode);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        doc.RootElement.TryGetProperty("result", out _).ShouldBeFalse();
    }

    private static string Result(string response)
    {
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.GetProperty("result").GetString()!;
    }

    private static CodeGraphMcpServer CreateCodeGraphServer(IGraphStore store) =>
        new(
            null!,
            null!,
            store,
            null!,
            Options.Create(new RepositorySourceOptions()),
            null!,
            null!);

    // Records which IMemoryOperationsService method each consolidated memory operation routes
    // to, and returns minimal canned results so the envelope can still be built.
    private sealed class RecordingMemoryOperationsService : IMemoryOperationsService
    {
        public List<string> Calls { get; } = [];
        public int? LastEntityLimit { get; private set; }
        public int? LastClaimLimit { get; private set; }

        public Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
            MemoryClaimExtractionResult extraction, string source, string inputMode, CancellationToken ct = default)
        {
            Calls.Add(nameof(QueueClaimsAsync));
            return Task.FromResult(new MemoryStoreAcceptedResult { ReceiptId = "memory_write_1", Status = "queued" });
        }

        public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetWriteReceiptAsync));
            return Task.FromResult<MemoryWriteReceipt?>(new MemoryWriteReceipt { Id = receiptId });
        }

        public Task<MemoryQueryResult> QueryAsync(string topic, int hops = 2, int maxNodes = 20, CancellationToken ct = default)
        {
            Calls.Add(nameof(QueryAsync));
            LastEntityLimit = maxNodes;
            return Task.FromResult(new MemoryQueryResult());
        }

        public Task<MemorySearchResult> SearchMemoryAsync(string query, int entityLimit = 5, int claimLimit = 5, CancellationToken ct = default)
        {
            Calls.Add(nameof(SearchMemoryAsync));
            LastEntityLimit = entityLimit;
            LastClaimLimit = claimLimit;
            return Task.FromResult(new MemorySearchResult());
        }

        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetMemorySubgraphAsync));
            LastEntityLimit = request.MaxReturnedEntities;
            LastClaimLimit = request.MaxReturnedClaims;
            return Task.FromResult(new MemorySubgraphResult());
        }

        public Task<MemoryEntityBundle?> GetEntityBundleAsync(
            string entityId, bool includeSuperseded = false, bool includeConflicts = true,
            int neighborLimit = 20, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetEntityBundleAsync));
            LastEntityLimit = neighborLimit;
            return Task.FromResult<MemoryEntityBundle?>(null);
        }

        public Task<MemoryClaimBundle?> GetClaimBundleAsync(
            string claimId, bool includeSupersessionChain = true, bool includeConflicts = true,
            bool includeEvidence = true, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetClaimBundleAsync));
            return Task.FromResult<MemoryClaimBundle?>(null);
        }

        public Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(
            MemoryFrontierExpansionRequest request, CancellationToken ct = default)
        {
            Calls.Add(nameof(ExpandMemoryFrontierAsync));
            LastEntityLimit = request.FrontierLimit;
            return Task.FromResult(new MemoryFrontierExpansionResult());
        }

        public Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(
            MemorySummaryRenderRequest request, CancellationToken ct = default)
        {
            Calls.Add(nameof(RenderMemorySummaryAsync));
            return Task.FromResult(new MemorySummaryRenderResult { Text = "summary" });
        }

        public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
            int staleAfterMinutes = 15, int sampleLimit = 10, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
            int staleAfterMinutes = 15, int sampleLimit = 10, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(string source, bool dryRun, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(bool dryRun, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
            IReadOnlyList<string> claimIds, IReadOnlyList<string> entityIds, bool dryRun, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryEntityWithRelationships?> GetEntityWithRelationshipsAsync(string entityId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
