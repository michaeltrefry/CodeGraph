using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class DreamingInstructionUpgradeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Theory]
    [InlineData("108_dreaming_tool_contract_instructions.sql", 2, 3)]
    [InlineData("109_dreaming_source_verification_instructions.sql", 3, 4)]
    public async Task Canonical_contract_upgrade_preserves_edited_pages_and_history_and_is_restart_safe(string script, int fromRevision, int toRevision)
    {
        var migrations = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var docs = Path.GetFullPath(Path.Combine(migrations, "../../docs/dreaming/base-instructions.md"));
        var statements = MariaDbMigrationRunner.SplitStatements(await File.ReadAllTextAsync(Path.Combine(migrations, script)));
        var update = statements.Single(statement => statement.Contains("SET p.content='"));
        var expected = System.Text.RegularExpressions.Regex.Match(update, "SET p\\.content='(?<content>(?:''|[^'])*)', p\\.revision=", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Groups["content"].Value.Replace("''", "'");
        expected.ShouldNotBeNullOrEmpty();
        if (toRevision == 4) expected.ShouldBe(await File.ReadAllTextAsync(docs));
        await using var c = new MySqlConnection(db.ConnectionString);
        var page = await c.ExecuteScalarAsync<long>("SELECT p.id FROM wiki_pages p JOIN wiki_sections s ON s.id=p.section_id WHERE p.slug='dreaming-base' AND p.parent_id IS NULL AND s.slug='conventions'");
        var old = await c.ExecuteScalarAsync<string>("SELECT content FROM wiki_revisions WHERE page_id=@page AND revision=@fromRevision", new { page, fromRevision });
        old.ShouldNotBeNullOrEmpty();
        // Model the installation before this upgrade, then a byte-different edited page at the same
        // revision. Only this test's disposable fixture database is changed.
        await c.ExecuteAsync("DELETE FROM wiki_revisions WHERE page_id=@page AND revision>@fromRevision", new { page, fromRevision });
        await c.ExecuteAsync("UPDATE wiki_pages SET content=@content,revision=@fromRevision,author='codegraph' WHERE id=@page", new { page, fromRevision, content = old + "\nA local edit." });
        async Task ApplyAsync() { foreach (var statement in statements) await c.ExecuteAsync(statement); }
        await ApplyAsync();
        (await c.ExecuteScalarAsync<string>("SELECT content FROM wiki_pages WHERE id=@page", new { page })).ShouldBe(old + "\nA local edit.");
        (await c.ExecuteScalarAsync<int>("SELECT revision FROM wiki_pages WHERE id=@page", new { page })).ShouldBe(fromRevision);
        await c.ExecuteAsync("UPDATE wiki_pages SET content=@old WHERE id=@page", new { page, old });
        await ApplyAsync();
        var store = new MySqlDreamingStore(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));
        var upgraded = (await store.GetInstructionsAsync(Guid.NewGuid())).Base;
        upgraded.Revision.ShouldBe(toRevision);
        upgraded.Content.ShouldBe(expected);
        upgraded.History.Single(revision => revision.Revision == fromRevision).Content.ShouldBe(old);
        upgraded.History.Single(revision => revision.Revision == toRevision).Content.ShouldBe(upgraded.Content);
        await ApplyAsync();
        (await store.GetInstructionsAsync(Guid.NewGuid())).Base.Revision.ShouldBe(toRevision);
        var custom = await store.SetInstructionsAsync(Guid.NewGuid(), "base", "A person's custom instructions", toRevision, new WorkActor("alice"));
        await ApplyAsync();
        var retained = (await store.GetInstructionsAsync(Guid.NewGuid())).Base;
        retained.Content.ShouldBe(custom.Content);
        retained.Revision.ShouldBe(custom.Revision);
        retained.History.Select(revision => revision.Content).ShouldBe(custom.History.Select(revision => revision.Content));
    }
}
