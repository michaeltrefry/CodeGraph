namespace CodeGraph.Services.Embeddings;

public interface IEmbeddingService
{
    bool IsAvailable { get; }
    string ModelName { get; }
    int Dimensions { get; }
    float[] GenerateEmbedding(string text);
    IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts);
}
