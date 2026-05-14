using CodeGraph.Data;
using CodeGraph.Services.WikiRag;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class MarkdownWikiChunkerTests
{
    [Fact]
    public void Chunk_PrefixesMarkdownBreadcrumbsAndSkipsFrontmatter()
    {
        var page = new WikiPageEntity
        {
            Id = 42,
            Slug = "messaging",
            Title = "Messaging",
            Content = """
                ---
                owner: platform
                ---
                # Overview

                Intro text.

                ## Consumers

                Prefer idempotent consumers.
                """,
            Revision = 3
        };

        var chunks = new MarkdownWikiChunker().Chunk(page, page.Title);

        chunks.Count.ShouldBe(2);
        chunks[0].EmbeddingText.ShouldStartWith("Messaging > # Overview");
        chunks[0].EmbeddingText.ShouldNotContain("owner: platform");
        chunks[1].EmbeddingText.ShouldStartWith("Messaging > # Overview > ## Consumers");
        chunks[1].EntityKey.ShouldBe("42:3:1");
    }
}
