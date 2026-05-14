using CodeGraph.Data;

namespace CodeGraph.Services.WikiRag;

public interface IMarkdownWikiChunker
{
    IReadOnlyList<ConventionChunk> Chunk(WikiPageEntity page, string sectionPath);
}
