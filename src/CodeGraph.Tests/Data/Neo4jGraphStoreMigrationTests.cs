using CodeGraph.Data.Neo4j;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jGraphStoreMigrationTests
{
    [Fact]
    public void ParseMigrationStatements_KeepsStatementsFollowingCommentHeaders()
    {
        const string cypher =
            """
            CREATE CONSTRAINT first IF NOT EXISTS
            FOR (n:Node) REQUIRE n.id IS UNIQUE;

            // Comment header
            CREATE FULLTEXT INDEX second IF NOT EXISTS
            FOR (n:Node) ON EACH [n.name];
            """;

        var statements = Neo4jGraphStore.ParseMigrationStatements(cypher);

        statements.Count.ShouldBe(2);
        statements[0].ShouldContain("CREATE CONSTRAINT first");
        statements[1].ShouldContain("CREATE FULLTEXT INDEX second");
    }
}
