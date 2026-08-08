using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Extractors;

public class TreeSitterExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    [Fact]
    public async Task ExtractAsync_CStructs_GetLineRanges()
    {
        var source = """
            typedef struct {
                int rpm;
                int current_ma;
            } motor_state_t;
            """;

        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var result = await extractor.ExtractAsync("/test/motor_state.h", source, TestContext);

        var structNode = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Struct &&
            n.Name == "motor_state_t");

        structNode.StartLine.ShouldBe(1);
        structNode.EndLine.ShouldBeGreaterThan(structNode.StartLine);
    }

    [Fact]
    public async Task ExtractAsync_RustCaseDistinctSymbols_GetStableSourceScopedQualifiedNames()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var typeResult = await extractor.ExtractAsync(
            "/test/crates/core/src/memory_calibration.rs",
            "pub struct Strategy { pub rung: u32 }",
            TestContext);
        var functionResult = await extractor.ExtractAsync(
            "/test/crates/worker/src/candle_memory_strategy.rs",
            "fn strategy(rung: u32) -> u32 { rung }",
            TestContext);

        var type = typeResult.Nodes.ShouldHaveSingleItem();
        var function = functionResult.Nodes.ShouldHaveSingleItem();

        type.QualifiedName.ShouldBe(
            "TestProject:crates/core/src/memory_calibration.rs#type:Strategy");
        function.QualifiedName.ShouldBe(
            "TestProject:crates/worker/src/candle_memory_strategy.rs#function:strategy(rung:u32)");
        string.Equals(type.QualifiedName, function.QualifiedName, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse();
        typeResult.Edges.ShouldContain(edge =>
            edge.SourceQN == "TestProject:crates/core/src/memory_calibration.rs" &&
            edge.TargetQN == type.QualifiedName);
        functionResult.Edges.ShouldContain(edge =>
            edge.SourceQN == "TestProject:crates/worker/src/candle_memory_strategy.rs" &&
            edge.TargetQN == function.QualifiedName);
    }
}
