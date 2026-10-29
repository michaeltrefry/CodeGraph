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

    [Fact]
    public void RepositoryRenameMigration_BatchesEveryPropertyRename()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../CodeGraph.Api/Migrations/014_rename_project_to_repository.cypher"));
        var statements = Neo4jGraphStore.ParseMigrationStatements(File.ReadAllText(path));

        // Property renames are the MATCH ... WHERE x.old IS NOT NULL statements (label swaps have no WHERE).
        var propertyRenames = statements
            .Where(s => s.StartsWith("MATCH", StringComparison.Ordinal) && s.Contains("IS NOT NULL", StringComparison.Ordinal))
            .ToList();

        propertyRenames.Count.ShouldBe(16);
        propertyRenames.ShouldAllBe(s => Neo4jGraphStore.RequiresAutoCommit(s));
        Neo4jGraphStore.RequiresAutoCommit("CREATE INDEX x IF NOT EXISTS FOR (n:A) ON (n.b)").ShouldBeFalse();
    }
}
