using CodeGraph.Data;

namespace CodeGraph.Services.WikiRag;

public sealed record ConventionChunk(
    long PageId,
    string Slug,
    string Title,
    string SectionPath,
    int Revision,
    int ChunkIndex,
    string Text,
    string EmbeddingText)
{
    public string EntityKey => $"{PageId}:{Revision}:{ChunkIndex}";
    public string PageKeyPrefix => $"{PageId}:";

    public static ConventionChunk FromPage(
        WikiPageEntity page,
        string sectionPath,
        int chunkIndex,
        string text,
        string embeddingText) =>
        new(page.Id, page.Slug, page.Title, sectionPath, page.Revision, chunkIndex, text, embeddingText);
}
