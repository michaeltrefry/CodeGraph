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
}
