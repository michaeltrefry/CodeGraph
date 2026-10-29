using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Search and trace answers on the default branch that include tree-sitter fallback nodes say so,
/// per node and once for the answer, in text and structuredContent.
/// </summary>
public sealed class LowConfidenceAnswerTests
{
    private const string Repo = "RustRepo";
    private readonly InMemoryGraphStore _store = new();
    private readonly long _fallbackId;
    private readonly long _semanticId;

    public LowConfidenceAnswerTests()
    {
        _store.AddRepository(Repo);
        _fallbackId = _store.AddNode(Node("fallback_fn", "crates/gamma/src/lib.rs", lowConfidence: true));
        _semanticId = _store.AddNode(Node("semantic_fn", "crates/beta/src/lib.rs", lowConfidence: false));
        _store.AddEdge(new GraphEdge { Repository = Repo, SourceId = _semanticId, TargetId = _fallbackId, Type = EdgeType.CALLS });
    }

    [Fact]
    public async Task SearchGraph_TagsLowConfidenceNodes_AndSummarizes()
    {
        var answer = await Mcp().SearchGraph("_fn", repository: Repo);

        var text = answer.Text();
        text.ShouldContain("**fallback_fn** (Function) — RustRepo [low confidence]");
        text.ShouldContain("**semantic_fn** (Function) — RustRepo\n");
        text.ShouldContain("**Low confidence:** this answer includes 1 low-confidence node(s)");
        answer.StructuredString("lowConfidence").ShouldBe("true");
        answer.StructuredString("lowConfidenceCount").ShouldBe("1");

        var semanticOnly = await Mcp().SearchGraph("semantic_fn", repository: Repo);
        semanticOnly.Text().ShouldNotContain("low confidence", Case.Insensitive);
        semanticOnly.StructuredString("lowConfidence").ShouldBe("false");

        var consolidated = await ConsolidatedMcpServer.CodegraphSearch(Mcp(), "search", "_fn", repository: Repo);
        using var envelope = JsonDocument.Parse(consolidated.Text());
        envelope.RootElement.GetProperty("lowConfidenceCount").GetInt32().ShouldBe(1);
    }

    // InMemoryGraphStore.TraverseAsync returns the start node itself as the only entry, so these
    // exercise per-entry tagging; real traversal (callers, empty results) is covered at a commit by
    // OverlayRustConfidenceTests.
    [Fact]
    public async Task TraceCallPath_TagsLowConfidenceEntries_AndSummarizes()
    {
        var fallback = await Mcp().TraceCallPath("fallback_fn", repository: Repo);

        fallback.Text().ShouldContain("- **fallback_fn** (Function) — RustRepo [low confidence]");
        fallback.Text().ShouldContain("includes 1 low-confidence node(s)");
        fallback.StructuredString("lowConfidenceCount").ShouldBe("1");
        // Default-branch answers carry no overlay mapping count.
        fallback.StructuredString("unmappedInboundEdges").ShouldBeNull();

        var semantic = await Mcp().TraceCallPath("semantic_fn", repository: Repo);
        semantic.Text().ShouldNotContain("low confidence", Case.Insensitive);
        semantic.StructuredString("lowConfidence").ShouldBe("false");

        var consolidated = await ConsolidatedMcpServer.GraphTrace(Mcp(), "call_path", "fallback_fn", repository: Repo);
        using var envelope = JsonDocument.Parse(consolidated.Text());
        envelope.RootElement.GetProperty("lowConfidence").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumersPublishersLineageAndNodeSource_TagLowConfidenceNodes()
    {
        var consumers = await Mcp().FindConsumers("fallback_fn", Repo);
        consumers.Text().ShouldContain("**fallback_fn** — RustRepo (CALLS) [low confidence]");
        consumers.Text().ShouldContain("includes 1 low-confidence node(s)");
        consumers.StructuredString("lowConfidenceCount").ShouldBe("1");

        var publishers = await Mcp().FindPublishers("fallback_fn", Repo);
        publishers.Text().ShouldContain("[low confidence]");
        publishers.StructuredString("lowConfidenceCount").ShouldBe("1");

        var semanticConsumers = await Mcp().FindConsumers("semantic_fn", Repo);
        semanticConsumers.StructuredString("lowConfidenceCount").ShouldBe("0");

        var lineage = await Mcp().TraceDataLineage("fallback_fn", Repo);
        lineage.Text().ShouldContain("Model: **fallback_fn** (Function) — RustRepo [low confidence]");
        lineage.StructuredString("lowConfidence").ShouldBe("true");

        var source = await ConsolidatedMcpServer.GraphSource(Mcp(), "node", nodeId: _fallbackId);
        using var envelope = JsonDocument.Parse(source.Text());
        envelope.RootElement.GetProperty("lowConfidence").GetBoolean().ShouldBeTrue();
    }

    private CodeGraphMcpServer Mcp() => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance));

    private static GraphNode Node(string name, string path, bool lowConfidence) => new()
    {
        Repository = Repo,
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"{Repo}:{path}#{name}",
        FilePath = path,
        StartLine = 1,
        EndLine = 2,
        Properties = lowConfidence
            ? new Dictionary<string, object>
            {
                [GraphNodeConfidence.ConfidenceProperty] = GraphNodeConfidence.Low,
                [GraphNodeConfidence.ExtractionSourceProperty] = GraphNodeConfidence.TreeSitterFallbackSource,
                [GraphNodeConfidence.FallbackReasonProperty] = "timeout"
            }
            : new Dictionary<string, object>()
    };
}
