using Shouldly;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Tests.Services;

public class StringExtensionsTests
{
    [Fact]
    public void NormalizeJsonResponse_RemovesThinkTags_AndReturnsJsonObject()
    {
        var text = """
            <think>
            Need to reason about the repository first.
            </think>
            {"repoSummary":"A service","confidence":"high"}
            """;

        var normalized = text.NormalizeJsonResponse();

        normalized.ShouldBe("""{"repoSummary":"A service","confidence":"high"}""");
    }

    [Fact]
    public void NormalizeJsonResponse_StripsCodeFences_AndLeadingProse()
    {
        var text = """
            Here's the JSON:

            ```json
            {"projectSummary":"Processes jobs","confidence":"medium"}
            ```

            Hope that helps.
            """;

        var normalized = text.NormalizeJsonResponse();

        normalized.ShouldBe("""{"projectSummary":"Processes jobs","confidence":"medium"}""");
    }

    [Fact]
    public void NormalizeJsonResponse_HandlesUnclosedThinkTag_ByReturningJsonTail()
    {
        var text = """
            <think>
            comparing alternatives
            {"projectSummary":"Indexes code","confidence":"low"
            """;

        var normalized = text.NormalizeJsonResponse();

        normalized.ShouldBe("{\"projectSummary\":\"Indexes code\",\"confidence\":\"low\"");
    }

    [Fact]
    public void NormalizeJsonResponse_PreservesBracesInsideStrings()
    {
        var text = """
            {"projectSummary":"Parses strings like {value} safely","confidence":"high"}
            trailing note
            """;

        var normalized = text.NormalizeJsonResponse();

        normalized.ShouldBe("""{"projectSummary":"Parses strings like {value} safely","confidence":"high"}""");
    }
}
