using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Metadata;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class IndexingPipelineTests
{
    [Fact]
    public async Task IndexProjectAsync_UsesDominantLanguageMetadata_InsteadOfFirstCompletedFile()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-language-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "build.sh"), "#!/usr/bin/env bash\necho hi\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.c"), "int main(void) { return 0; }\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.h"), "#pragma once\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestMetadataExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 3 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("DryBox", rootPath, ct: CancellationToken.None);

            var repo = await store.GetRepositoryByName("DryBox");
            repo.ShouldNotBeNull();
            repo.Language.ShouldBe("C");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_PersistsDotnetSupportMetadata_InRepositoryProperties()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-support-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "legacy.cs"), "public class Legacy {}");

            var support = new DotnetSupportInfo(
                "out_of_support",
                "Pinned SDK 2.1.802 is out of support.",
                new DotnetSdkSupportInfo("2.1.802", "2.1", ".NET SDK 2.1", "out_of_support", new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc), true),
                [new DotnetTargetFrameworkSupportInfo("netcoreapp2.1", ".NET Core 2.1", "out_of_support", new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc))]);

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestSupportMetadataExtractor(support)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("LegacyRepo", rootPath, ct: CancellationToken.None);

            var repo = await store.GetRepositoryByName("LegacyRepo");
            repo.ShouldNotBeNull();
            var storedSupport = DotnetSupportInspector.TryReadStoredSupport(repo.Properties);
            storedSupport.ShouldNotBeNull();
            storedSupport.Sdk.ShouldNotBeNull();
            storedSupport.Sdk.Version.ShouldBe("2.1.802");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_UsesSlnxForSolutionLevelAnalysis()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-slnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var solutionPath = Path.Combine(rootPath, "Demo.slnx");
            await File.WriteAllTextAsync(solutionPath, "<Solution />");

            var store = new InMemoryGraphStore();
            var solutionAnalyzer = new RecordingSolutionAnalyzer();
            var pipeline = new IndexingPipeline(
                store,
                [],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                solutionAnalyzer);

            await pipeline.IndexProjectAsync("Demo", rootPath, ct: CancellationToken.None);

            solutionAnalyzer.CalledSolutionPath.ShouldBe(solutionPath);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_AssignsStructuralFilesToNearestDotnetProject()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-dotnet-project-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var appDir = Path.Combine(rootPath, "src", "Demo.App");
            Directory.CreateDirectory(appDir);

            await File.WriteAllTextAsync(Path.Combine(appDir, "Demo.App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Program.cs"), "public class Program {}");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestSupportMetadataExtractor(new DotnetSupportInfo("supported", "supported", null, []))],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("DemoRepo", rootPath, ct: CancellationToken.None);

            var counts = await store.GetNodeCountsByDotnetProjectAsync("DemoRepo");
            counts.ShouldContainKey("Demo.App");
            counts["Demo.App"].ShouldContainKey(nameof(NodeLabel.File));
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class TestMetadataExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".sh", ".c", ".h"], StringComparer.OrdinalIgnoreCase);

        public async Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var extension = Path.GetExtension(filePath);
            var delay = extension.ToLowerInvariant() switch
            {
                ".sh" => 1,
                ".c" => 50,
                ".h" => 50,
                _ => 1
            };

            await Task.Delay(delay, ct);

            var metadata = extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
                ? new ProjectMetadata("Bash", null)
                : new ProjectMetadata("C", null);

            return new ExtractionResult
            {
                Metadata = metadata
            };
        }
    }

    private sealed class TestSupportMetadataExtractor(DotnetSupportInfo support) : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ExtractionResult
            {
                Metadata = new ProjectMetadata("C#", ".NET", support)
            });
        }
    }

    private sealed class RecordingSolutionAnalyzer : ISolutionAnalyzer
    {
        public string? CalledSolutionPath { get; private set; }

        public Task<IReadOnlyList<ExtractionResult>> AnalyzeSolutionAsync(
            string solutionPath,
            ExtractorContext context,
            CancellationToken ct)
        {
            CalledSolutionPath = solutionPath;
            return Task.FromResult<IReadOnlyList<ExtractionResult>>([]);
        }
    }
}
