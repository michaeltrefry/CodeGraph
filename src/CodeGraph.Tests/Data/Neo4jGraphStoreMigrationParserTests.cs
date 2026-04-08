using CodeGraph.Data.Neo4j;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jGraphStoreMigrationParserTests
{
    [Fact]
    public void ParseMigrationStatements_KeepsStatementsAfterCommentHeaders()
    {
        var cypher = """
            // Header comment
            CREATE CONSTRAINT first_statement IF NOT EXISTS
            FOR (n:Thing) REQUIRE n.id IS UNIQUE;

            // Another header
            CREATE INDEX second_statement IF NOT EXISTS
            FOR (n:Thing) ON (n.name);
            """;

        var statements = Neo4jGraphStore.ParseMigrationStatements(cypher);

        statements.Count.ShouldBe(2);
        statements[0].ShouldStartWith("CREATE CONSTRAINT first_statement");
        statements[1].ShouldStartWith("CREATE INDEX second_statement");
    }
}
