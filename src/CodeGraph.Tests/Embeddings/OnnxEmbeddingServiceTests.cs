using CodeGraph.Services.Embeddings;
using Shouldly;

namespace CodeGraph.Tests.Embeddings;

public class OnnxEmbeddingServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codegraph-onnx-tests-{Guid.NewGuid():N}");

    public OnnxEmbeddingServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ResolveModelPath_ReturnsConfiguredPath_WhenItExists()
    {
        var modelPath = Path.Combine(_tempDir, "model.onnx");
        File.WriteAllText(modelPath, "test");

        OnnxEmbeddingService.ResolveModelPath(modelPath).ShouldBe(modelPath);
    }

    [Fact]
    public void ResolveModelPath_ReturnsNull_WhenConfiguredPathDoesNotExist()
    {
        OnnxEmbeddingService.ResolveModelPath(
            Path.Combine(_tempDir, "models", "embeddings", "nomic-embed-text-v1.5", "model.onnx"))
            .ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
