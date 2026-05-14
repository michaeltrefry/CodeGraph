using CodeGraph.Data;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.WikiRag;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ConventionEmbeddingServiceTests
{
    [Fact]
    public async Task IngestAllAsync_StoresMarkdownAwareConventionChunkEmbeddings()
    {
        var wikiStore = new FakeWikiStore();
        var vectorStore = new FakeVectorStore();
        var sut = new ConventionEmbeddingService(
            wikiStore,
            vectorStore,
            new FakeEmbeddingService(),
            new MarkdownWikiChunker(),
            NullLogger<ConventionEmbeddingService>.Instance);

        var count = await sut.IngestAllAsync();

        count.ShouldBe(2);
        vectorStore.Stored.Select(item => item.entityKey).ShouldBe(["100:2:0", "100:2:1"]);
        vectorStore.DeletedPrefixes.ShouldContain("100:");
    }

    [Fact]
    public async Task SearchAsync_CombinesLexicalAndDenseResults()
    {
        var wikiStore = new FakeWikiStore();
        var vectorStore = new FakeVectorStore();
        vectorStore.Results =
        [
            new VectorSearchResult(ConventionEmbeddingService.EntityType, "100:2:1", 0.9)
        ];
        var sut = new ConventionEmbeddingService(
            wikiStore,
            vectorStore,
            new FakeEmbeddingService(),
            new MarkdownWikiChunker(),
            NullLogger<ConventionEmbeddingService>.Instance);

        var results = await sut.SearchAsync("idempotent consumers", 3);

        results.Count.ShouldBeGreaterThan(0);
        results[0].Slug.ShouldBe("messaging");
        results[0].SectionPath.ShouldContain("Consumers");
        results[0].Excerpt.ShouldContain("idempotent consumers");
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool IsAvailable => true;
        public string ModelName => "test";
        public int Dimensions => 3;
        public float[] GenerateEmbedding(string text) => [1f, 0f, 0f];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) =>
            texts.Select(_ => new[] { 1f, 0f, 0f }).ToList();
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public List<(string entityType, string entityKey, float[] embedding)> Stored { get; } = [];
        public List<string> DeletedPrefixes { get; } = [];
        public IReadOnlyList<VectorSearchResult> Results { get; set; } = [];

        public Task StoreEmbeddingAsync(string entityType, string entityKey, float[] embedding)
        {
            Stored.Add((entityType, entityKey, embedding));
            return Task.CompletedTask;
        }

        public Task StoreBatchEmbeddingsAsync(IReadOnlyList<(string entityType, string entityKey, float[] embedding)> items)
        {
            Stored.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
            float[] queryEmbedding,
            string? entityType = null,
            int topK = 10,
            double minScore = 0.5) => Task.FromResult(Results);

        public Task DeleteEmbeddingsAsync(string entityType, string entityKey) => Task.CompletedTask;

        public Task DeleteEmbeddingsByKeyPrefixAsync(string entityType, string entityKeyPrefix)
        {
            DeletedPrefixes.Add(entityKeyPrefix);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWikiStore : IWikiStore
    {
        private readonly WikiSectionEntity _section = new()
        {
            Id = 10,
            Slug = "conventions",
            Title = "Conventions"
        };

        private readonly WikiPageEntity _page = new()
        {
            Id = 100,
            SectionId = 10,
            Slug = "messaging",
            Title = "Messaging",
            Content = """
                # Messaging

                Use events for asynchronous integration.

                ## Consumers

                Prefer idempotent consumers with stable retry behavior.
                """,
            Author = "codex",
            Revision = 2
        };

        public Task<IReadOnlyList<WikiSectionEntity>> ListSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionEntity?> GetSectionBySlugAsync(string slug) => Task.FromResult<WikiSectionEntity?>(slug == "conventions" ? _section : null);
        public Task<WikiSectionEntity?> GetSectionByIdAsync(long id) => Task.FromResult<WikiSectionEntity?>(id == _section.Id ? _section : null);
        public Task<int> CountSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionEntity> CreateSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task UpdateSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task DeleteSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task<WikiPageEntity?> GetPageByIdAsync(long id) => Task.FromResult<WikiPageEntity?>(id == _page.Id ? _page : null);
        public Task<WikiPageEntity?> FindPageAsync(long sectionId, long? parentId, string slug) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiPageEntity>> GetPagesBySectionAsync(long sectionId) => Task.FromResult<IReadOnlyList<WikiPageEntity>>([_page]);
        public Task<IReadOnlyList<WikiPageEntity>> GetAutoGeneratedPagesBySectionAsync(long sectionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiPageEntity>> SearchPagesAsync(long sectionId, string pattern) => throw new NotSupportedException();
        public Task<int> GetMaxSortOrderAsync(long sectionId, long? parentId) => throw new NotSupportedException();
        public Task<WikiPageEntity> CreatePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task UpdatePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task DeletePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiRevisionEntity>> GetRevisionsAsync(long pageId) => throw new NotSupportedException();
        public Task<WikiRevisionEntity?> GetRevisionAsync(long pageId, int revision) => throw new NotSupportedException();
        public Task CreateRevisionAsync(WikiRevisionEntity entity) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiAttachmentEntity>> ListAttachmentsAsync(long pageId) => throw new NotSupportedException();
        public Task<WikiAttachmentEntity?> GetAttachmentByIdAsync(long id) => throw new NotSupportedException();
        public Task<WikiAttachmentEntity> CreateAttachmentAsync(WikiAttachmentEntity entity) => throw new NotSupportedException();
        public Task DeleteAttachmentAsync(WikiAttachmentEntity entity) => throw new NotSupportedException();
    }
}
