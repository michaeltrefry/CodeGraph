using CodeGraph.Data.Neo4j;
using Neo4j.Driver;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jMemoryGraphStoreTests
{
    [Fact]
    public void IsMissingMemoryFulltextIndex_ReturnsTrue_ForExpectedNeo4jError()
    {
        var ex = new ClientException(
            "Neo.ClientError.Procedure.ProcedureCallFailed",
            "Failed to invoke procedure `db.index.fulltext.queryNodes`: Caused by: " +
            "java.lang.IllegalArgumentException: There is no such fulltext schema index: memory_entity_fulltext");

        Neo4jMemoryGraphStore.IsMissingMemoryFulltextIndex(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsMissingMemoryFulltextIndex_ReturnsFalse_ForOtherNeo4jErrors()
    {
        var ex = new ClientException(
            "Neo.ClientError.Statement.SyntaxError",
            "Some other Neo4j problem");

        Neo4jMemoryGraphStore.IsMissingMemoryFulltextIndex(ex).ShouldBeFalse();
    }
}
