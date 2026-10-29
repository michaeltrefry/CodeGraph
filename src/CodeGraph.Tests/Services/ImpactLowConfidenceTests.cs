using System.Text.Json;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ImpactLowConfidenceTests
{
    [Fact]
    public async Task AnalyzeImpact_OnTreeSitterFallbackNode_SaysAnswerIncludesLowConfidenceNodes()
    {
        var store = new InMemoryGraphStore();
        store.AddNode(CreateNode("alpha_value", new Dictionary<string, object>
        {
            [GraphNodeConfidence.ConfidenceProperty] = GraphNodeConfidence.Low,
            [GraphNodeConfidence.ExtractionSourceProperty] = GraphNodeConfidence.TreeSitterFallbackSource,
            [GraphNodeConfidence.FallbackReasonProperty] = "timeout"
        }));
        var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);

        var report = await impact.AnalyzeImpactAsync("RustRepo:alpha_value", "RustRepo");
        report.ShouldNotBeNull();
        report.Summary.LowConfidenceCount.ShouldBe(1);
        report.ChangedNodes.ShouldHaveSingleItem().LowConfidence.ShouldBeTrue();

        var answer = (await CreateMcpServer(store, impact).AnalyzeImpact("RustRepo:alpha_value", "RustRepo")).Text();

        answer.ShouldContain("includes 1 low-confidence node(s)");
        answer.ShouldContain("[low confidence]");
    }

    [Fact]
    public async Task AnalyzeImpact_RecognizesLowConfidenceMarkReadBackFromJson()
    {
        var store = new InMemoryGraphStore();
        var persisted = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"confidence":"low","extractionSource":"tree-sitter-fallback"}""")!;
        store.AddNode(CreateNode("beta_value", persisted));
        var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);

        var report = await impact.AnalyzeImpactAsync("RustRepo:beta_value", "RustRepo");

        report!.Summary.LowConfidenceCount.ShouldBe(1);
    }

    [Fact]
    public async Task AnalyzeImpact_OnSemanticNode_DoesNotMentionLowConfidence()
    {
        var store = new InMemoryGraphStore();
        store.AddNode(CreateNode("gamma_value", new Dictionary<string, object> { ["confidence"] = "high" }));
        var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);

        var answer = (await CreateMcpServer(store, impact).AnalyzeImpact("RustRepo:gamma_value", "RustRepo")).Text();

        answer.ShouldNotContain("low-confidence");
        answer.ShouldNotContain("[low confidence]");
    }

    private static GraphNode CreateNode(string name, Dictionary<string, object> properties) => new()
    {
        Repository = "RustRepo",
        Label = NodeLabel.Function,
        Name = name,
        QualifiedName = $"RustRepo:{name}",
        FilePath = "crates/alpha/src/lib.rs",
        Properties = properties
    };

    private static CodeGraphMcpServer CreateMcpServer(InMemoryGraphStore store, ImpactAnalysisService impact) =>
        new(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!, impact);
}
