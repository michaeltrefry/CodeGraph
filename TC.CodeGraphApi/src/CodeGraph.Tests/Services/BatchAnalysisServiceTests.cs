using Shouldly;
using CodeGraph.Services;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisServiceTests
{
    [Fact]
    public void CompressMethodBodies_PreservesSignatures_CollapsesBody()
    {
        var lines = new[]
        {
            "public class OrderService",
            "{",
            "    public async Task<Order> GetOrderAsync(int id)",
            "    {",
            "        var order = await _repo.FindAsync(id);",
            "        if (order is null) throw new NotFoundException();",
            "        order.Validate();",
            "        return order;",
            "    }",
            "}"
        };

        var result = BatchAnalysisService.CompressMethodBodies(lines);

        // Should keep class declaration, method signature, opening brace
        result.ShouldContain(l => l.Contains("public class OrderService"));
        result.ShouldContain(l => l.Contains("GetOrderAsync"));
        // Should collapse the body lines into a comment
        result.ShouldContain(l => l.Contains("// ..."));
        // Should keep closing braces
        result.Count(l => l.Trim() == "}").ShouldBe(2);
    }

    [Fact]
    public void CompressMethodBodies_SkipsUsingsAndXmlDoc()
    {
        var lines = new[]
        {
            "using System;",
            "/// <summary>",
            "/// Does stuff",
            "/// </summary>",
            "public class Foo",
            "{",
            "}"
        };

        var result = BatchAnalysisService.CompressMethodBodies(lines);

        result.ShouldNotContain(l => l.Contains("using System"));
        result.ShouldNotContain(l => l.Contains("///"));
        result.ShouldContain(l => l.Contains("public class Foo"));
    }

    [Fact]
    public void CompressMethodBodies_SkipsBlankLines()
    {
        var lines = new[]
        {
            "public class Foo",
            "{",
            "",
            "    public void Bar() { }",
            "",
            "}"
        };

        var result = BatchAnalysisService.CompressMethodBodies(lines);

        result.ShouldNotContain(l => string.IsNullOrWhiteSpace(l));
    }

    [Fact]
    public void CompressMethodBodies_EmptyInput_ReturnsEmpty()
    {
        var result = BatchAnalysisService.CompressMethodBodies([]);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void CompressMethodBodies_SingleLineMethod_NoCollapse()
    {
        var lines = new[]
        {
            "public class Foo",
            "{",
            "    public int Id { get; set; }",
            "}"
        };

        var result = BatchAnalysisService.CompressMethodBodies(lines);

        // Property should be preserved as-is
        result.ShouldContain(l => l.Contains("Id { get; set; }"));
    }

    [Fact]
    public void CompressMethodBodies_NestedBraces_TracksDepthCorrectly()
    {
        var lines = new[]
        {
            "public class Svc",
            "{",
            "    public void Process()",
            "    {",
            "        if (true)",
            "        {",
            "            DoSomething();",
            "        }",
            "        else",
            "        {",
            "            DoOther();",
            "        }",
            "    }",
            "}"
        };

        var result = BatchAnalysisService.CompressMethodBodies(lines);

        // Method signature kept, body collapsed
        result.ShouldContain(l => l.Contains("Process()"));
        result.ShouldContain(l => l.Contains("// ..."));
        // Only class + method closing braces remain visible
        result.Count.ShouldBeLessThan(lines.Length);
    }
}
