namespace CodeGraph.Services.WikiRag;

public interface IConventionEmbeddingService
{
    Task<int> IngestAllAsync(CancellationToken ct = default);
    Task<int> ReindexPageAsync(long pageId, bool deleted, CancellationToken ct = default);
    Task<IReadOnlyList<ConventionSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
}

public sealed record ConventionSearchResult(
    string Slug,
    string Title,
    string SectionPath,
    int Revision,
    int ChunkIndex,
    double Score,
    string Excerpt);
