using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbWikiStoreTests
{
    [Fact]
    public void MySqlWikiStore_ImplementsStandaloneWikiContract()
    {
        typeof(IWikiStore).IsAssignableFrom(typeof(MySqlWikiStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlWikiStore_RoundTripsSectionsPagesRevisionsAndAttachmentsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_wiki_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(options);
            var store = new MySqlWikiStore(context);
            var now = DateTime.UtcNow;

            var section = await store.CreateSectionAsync(new WikiSectionEntity
            {
                Slug = "guides",
                Title = "Guides",
                SortOrder = 1,
                AllowUserPages = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            var page = await store.CreatePageAsync(new WikiPageEntity
            {
                SectionId = section.Id,
                Slug = "getting-started",
                Title = "Getting Started",
                Content = "Start here",
                RawContent = "# Start here",
                Author = "codex",
                SortOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            });

            await store.CreateRevisionAsync(new WikiRevisionEntity
            {
                PageId = page.Id,
                Revision = 1,
                Title = page.Title,
                Content = page.Content,
                RawContent = page.RawContent,
                Author = page.Author,
                CreatedAt = now
            });

            var attachment = await store.CreateAttachmentAsync(new WikiAttachmentEntity
            {
                PageId = page.Id,
                Filename = "notes.txt",
                StoragePath = "wiki/guides/notes.txt",
                ContentType = "text/plain",
                SizeBytes = 12,
                UploadedBy = "codex",
                CreatedAt = now
            });

            (await store.CountSectionsAsync()).ShouldBeGreaterThanOrEqualTo(1);
            (await store.GetSectionBySlugAsync("guides"))!.Id.ShouldBe(section.Id);
            (await store.FindPageAsync(section.Id, null, "getting-started"))!.Id.ShouldBe(page.Id);
            (await store.SearchPagesAsync(section.Id, "start")).Single().Id.ShouldBe(page.Id);
            (await store.GetMaxSortOrderAsync(section.Id, null)).ShouldBe(2);
            (await store.GetRevisionsAsync(page.Id)).Single().Revision.ShouldBe(1);
            (await store.GetRevisionAsync(page.Id, 1))!.Title.ShouldBe(page.Title);
            (await store.ListAttachmentsAsync(page.Id)).Single().Id.ShouldBe(attachment.Id);
            (await store.GetAttachmentByIdAsync(attachment.Id))!.Filename.ShouldBe("notes.txt");
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
