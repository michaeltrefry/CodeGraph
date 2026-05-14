using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Services.Embeddings;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.WikiRag;

public partial class ConventionEmbeddingService(
    IWikiStore wikiStore,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    IMarkdownWikiChunker chunker,
    ILogger<ConventionEmbeddingService> logger) : IConventionEmbeddingService
{
    public const string EntityType = "ConventionChunk";

    public async Task<int> IngestAllAsync(CancellationToken ct = default)
    {
        var pages = await GetConventionPagesAsync();
        var indexed = 0;

        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            indexed += await IndexPageAsync(page);
        }

        logger.LogInformation("Indexed {ChunkCount} convention chunks across {PageCount} pages", indexed, pages.Count);
        return indexed;
    }

    public async Task<int> ReindexPageAsync(long pageId, bool deleted, CancellationToken ct = default)
    {
        await vectorStore.DeleteEmbeddingsByKeyPrefixAsync(EntityType, $"{pageId}:");
        if (deleted)
            return 0;

        var page = await wikiStore.GetPageByIdAsync(pageId);
        if (page is null)
            return 0;

        var section = await wikiStore.GetSectionByIdAsync(page.SectionId);
        if (section?.Slug != "conventions")
            return 0;

        ct.ThrowIfCancellationRequested();
        return await IndexPageAsync(page);
    }

    public async Task<IReadOnlyList<ConventionSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        topK = Math.Clamp(topK, 1, 50);
        var pages = await GetConventionPagesAsync();
        var chunks = pages.SelectMany(BuildChunks).ToList();
        if (chunks.Count == 0)
            return [];

        var lexicalScores = ScoreBm25(query, chunks);
        var denseScores = await ScoreDenseAsync(query, topK, ct);
        var maxLexical = lexicalScores.Count == 0 ? 0 : lexicalScores.Values.Max();

        return chunks
            .Select(chunk =>
            {
                var lexical = lexicalScores.GetValueOrDefault(chunk.EntityKey);
                var normalizedLexical = maxLexical > 0 ? lexical / maxLexical : 0;
                var dense = denseScores.GetValueOrDefault(chunk.EntityKey);
                var score = (dense * 0.65) + (normalizedLexical * 0.35);
                return new { chunk, score };
            })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.chunk.Title)
            .Take(topK)
            .Select(item => new ConventionSearchResult(
                item.chunk.Slug,
                item.chunk.Title,
                item.chunk.SectionPath,
                item.chunk.Revision,
                item.chunk.ChunkIndex,
                item.score,
                BuildExcerpt(item.chunk.Text, query)))
            .ToList();
    }

    private async Task<int> IndexPageAsync(WikiPageEntity page)
    {
        await vectorStore.DeleteEmbeddingsByKeyPrefixAsync(EntityType, $"{page.Id}:");

        var chunks = BuildChunks(page);
        if (!embeddingService.IsAvailable || chunks.Count == 0)
            return 0;

        var embeddings = embeddingService.GenerateEmbeddings(chunks.Select(chunk => chunk.EmbeddingText).ToList());
        var items = chunks
            .Select((chunk, i) => (EntityType, chunk.EntityKey, embeddings[i]))
            .ToList();

        await vectorStore.StoreBatchEmbeddingsAsync(items);
        return chunks.Count;
    }

    private async Task<IReadOnlyList<WikiPageEntity>> GetConventionPagesAsync()
    {
        var section = await wikiStore.GetSectionBySlugAsync("conventions");
        return section is null ? [] : await wikiStore.GetPagesBySectionAsync(section.Id);
    }

    private IReadOnlyList<ConventionChunk> BuildChunks(WikiPageEntity page) =>
        chunker.Chunk(page, page.Title);

    private async Task<Dictionary<string, double>> ScoreDenseAsync(string query, int topK, CancellationToken ct)
    {
        if (!embeddingService.IsAvailable)
            return [];

        ct.ThrowIfCancellationRequested();
        var queryEmbedding = embeddingService.GenerateEmbedding(query);
        var results = await vectorStore.SearchSimilarAsync(queryEmbedding, EntityType, topK * 4, minScore: 0);
        return results.ToDictionary(result => result.EntityKey, result => Math.Max(0, result.Score));
    }

    private static Dictionary<string, double> ScoreBm25(string query, IReadOnlyList<ConventionChunk> chunks)
    {
        var queryTerms = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queryTerms.Count == 0)
            return [];

        var docs = chunks
            .Select(chunk => new { chunk.EntityKey, Terms = Tokenize(chunk.EmbeddingText).ToList() })
            .ToList();
        var avgDocLength = Math.Max(1, docs.Average(doc => doc.Terms.Count));
        var docFreq = queryTerms.ToDictionary(
            term => term,
            term => docs.Count(doc => doc.Terms.Contains(term, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        const double k1 = 1.2;
        const double b = 0.75;
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in docs)
        {
            var score = 0d;
            foreach (var term in queryTerms)
            {
                var frequency = doc.Terms.Count(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
                if (frequency == 0)
                    continue;

                var idf = Math.Log(1 + ((docs.Count - docFreq[term] + 0.5) / (docFreq[term] + 0.5)));
                var denominator = frequency + k1 * (1 - b + b * (doc.Terms.Count / avgDocLength));
                score += idf * ((frequency * (k1 + 1)) / denominator);
            }

            if (score > 0)
                scores[doc.EntityKey] = score;
        }

        return scores;
    }

    private static IReadOnlyList<string> Tokenize(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToList();

    private static string BuildExcerpt(string text, string query)
    {
        var terms = Tokenize(query);
        var firstHit = terms
            .Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, firstHit - 120);
        var length = Math.Min(360, text.Length - start);
        var excerpt = text.Substring(start, length).Trim();
        return WhitespaceRegex().Replace(excerpt, " ");
    }

    [GeneratedRegex(@"[a-z0-9][a-z0-9_\-]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
