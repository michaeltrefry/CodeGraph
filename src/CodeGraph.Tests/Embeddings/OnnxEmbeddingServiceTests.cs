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
    public void ResolveModelPath_FallsBackFromModelsMount_ToLegacyDockerMount()
    {
        var mountedRoot = Path.Combine(_tempDir, "models");
        var legacyRoot = Path.Combine(_tempDir, "legacy-models");
        var legacyModelPath = Path.Combine(legacyRoot, "embeddings", "all-MiniLM-L6-v2", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        File.WriteAllText(legacyModelPath, "test");

        OnnxEmbeddingService.ResolveModelPath(
            Path.Combine(mountedRoot, "embeddings", "all-MiniLM-L6-v2", "model.onnx"),
            [(mountedRoot + Path.DirectorySeparatorChar, legacyRoot + Path.DirectorySeparatorChar)])
            .ShouldBe(legacyModelPath);
    }

    [Fact]
    public void ResolveModelPath_ReturnsNull_WhenNeitherConfiguredNorFallbackPathExists()
    {
        OnnxEmbeddingService.ResolveModelPath(
            Path.Combine(_tempDir, "models", "embeddings", "all-MiniLM-L6-v2", "model.onnx"),
            [(Path.Combine(_tempDir, "models") + Path.DirectorySeparatorChar,
              Path.Combine(_tempDir, "missing-legacy-models") + Path.DirectorySeparatorChar)])
            .ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
