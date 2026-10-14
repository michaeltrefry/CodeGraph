using System.Text.Json;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TreeSitter;

namespace CodeGraph.Tests.Extractors;

public class SwiftTreeSitterExtractorTests
{
    private const string FileQN = "TestProject:Sources/Shapes.swift";

    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static string FixtureSource => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Swift", "Shapes.swift"));

    private static Task<ExtractionResult> ExtractFixtureAsync() =>
        new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)
            .ExtractAsync("/test/Sources/Shapes.swift", FixtureSource, TestContext);

    [Fact]
    public void SwiftGrammar_LoadsFromBuiltNativeLibrary()
    {
        using var language = new Language("Swift");

        language.Name.ShouldBe("swift");
        language.AbiVersion.ShouldBeInRange(13u, 15u);
    }

    [Fact]
    public void SwiftSpec_IsRegisteredForSwiftFilesWithItsOwnExtractionVersion()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        extractor.SupportedExtensions.ShouldContain(".swift");
        LanguageSpecs.ForExtension(".swift").ShouldBeSameAs(LanguageSpecs.Swift);
        LanguageSpecs.Swift.LanguageName.ShouldBe("Swift");
        LanguageSpecs.Swift.ExtractionVersion.ShouldBe(1);
        extractor.GetExtractionVersion(".swift")
            .ShouldBe($"{TreeSitterExtractor.WalkerVersion}.1");
    }

    [Fact]
    public async Task ExtractAsync_SwiftFixture_YieldsTypesWithKindSpecificLabels()
    {
        var result = await ExtractFixtureAsync();

        result.Succeeded.ShouldBeTrue();
        result.Metadata.ShouldNotBeNull();
        result.Metadata.Language.ShouldBe("Swift");

        AssertType(result, "Shape", NodeLabel.Interface, "protocol", $"{FileQN}#type:Shape", FileQN);
        AssertType(result, "Circle", NodeLabel.Struct, "struct", $"{FileQN}#type:Circle", FileQN);
        AssertType(result, "ShapeStore", NodeLabel.Class, "class", $"{FileQN}#type:ShapeStore", FileQN);
        AssertType(result, "Unit", NodeLabel.Enum, "enum", $"{FileQN}#type:Unit", FileQN);
        AssertType(result, "Counter", NodeLabel.Class, "actor", $"{FileQN}#type:Counter", FileQN);
        AssertType(result, "SortOrder", NodeLabel.Enum, "enum",
            $"{FileQN}#type:ShapeStore#type:SortOrder", $"{FileQN}#type:ShapeStore");
    }

    [Fact]
    public async Task ExtractAsync_SwiftFixture_YieldsFunctionsAndMethodsUnderTheirParents()
    {
        var result = await ExtractFixtureAsync();

        var totalArea = AssertCallable(result, "totalArea", NodeLabel.Function, FileQN);
        totalArea.QualifiedName.ShouldBe($"{FileQN}#function:totalArea(ofshapes:[Shape])");

        // Protocol requirements are members of the protocol.
        AssertCallable(result, "area", NodeLabel.Method, $"{FileQN}#type:Shape");
        AssertCallable(result, "describe", NodeLabel.Method, $"{FileQN}#type:Shape");

        AssertCallable(result, "area", NodeLabel.Method, $"{FileQN}#type:Circle");
        AssertCallable(result, "describe", NodeLabel.Method, $"{FileQN}#type:Circle")
            .QualifiedName.ShouldBe($"{FileQN}#type:Circle#method:describe(prefix:String)");
        AssertCallable(result, "increment", NodeLabel.Method, $"{FileQN}#type:Counter");
        AssertCallable(result, "reversed", NodeLabel.Method,
            $"{FileQN}#type:ShapeStore#type:SortOrder");

        // Overloads that differ only by argument label keep distinct identities.
        var adds = result.Nodes
            .Where(node => node.Name == "add" && node.Label == NodeLabel.Method)
            .Select(node => node.QualifiedName)
            .ToList();
        adds.ShouldBe(
        [
            $"{FileQN}#type:ShapeStore#method:add(_shape:Element)",
            $"{FileQN}#type:ShapeStore#method:add(contentsOfmore:[Element])"
        ], ignoreOrder: true);
        foreach (var add in adds)
            result.Edges.ShouldContain(edge =>
                edge.SourceQN == $"{FileQN}#type:ShapeStore" &&
                edge.TargetQN == add &&
                edge.Type == EdgeType.DEFINES_METHOD);
    }

    [Fact]
    public async Task ExtractAsync_SwiftExtension_DoesNotDuplicateTheExtendedType()
    {
        var result = await ExtractFixtureAsync();

        result.Nodes.Count(node => node.Name == "Circle").ShouldBe(1);
        result.Nodes.ShouldNotContain(node => node.QualifiedName.Contains("extension"));

        // Members of an extension of a type declared in the same file attach to that type.
        AssertCallable(result, "diameter", NodeLabel.Method, $"{FileQN}#type:Circle");
    }

    [Fact]
    public async Task ExtractAsync_SwiftExtensionOfUnknownType_KeepsMemberWithoutInventingAType()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            "/test/Sources/Shapes.swift",
            "extension Array {\n    func second() -> Element? { return dropFirst().first }\n}\n",
            TestContext);

        result.Nodes.ShouldNotContain(node => node.Name == "Array");
        var second = result.Nodes.ShouldHaveSingleItem();
        second.Name.ShouldBe("second");
        second.Label.ShouldBe(NodeLabel.Method);
        second.Properties["receiver_owner"].ShouldBe("Array");
    }

    [Fact]
    public async Task ExtractAsync_SwiftDefinitions_CarryLineRanges()
    {
        var result = await ExtractFixtureAsync();

        var circle = result.Nodes.Single(node => node.Name == "Circle");
        circle.StartLine.ShouldBe(8);
        circle.EndLine.ShouldBe(18);
    }

    [Fact]
    public async Task ExtractAsync_SwiftGrammarUnavailable_LogsAndSkipsFileInsteadOfFailing()
    {
        var brokenSwift = new LanguageSpec
        {
            LanguageName = LanguageSpecs.Swift.LanguageName,
            Framework = null,
            GetLanguage = () => throw new DllNotFoundException("libtree-sitter-swift"),
            SkipFileOnExtractionFailure = LanguageSpecs.Swift.SkipFileOnExtractionFailure
        };
        var extractor = new TreeSitterExtractor(
            NullLogger<TreeSitterExtractor>.Instance,
            extension => extension == ".swift" ? brokenSwift : null);

        var result = await extractor.ExtractAsync(
            "/test/Sources/Shapes.swift", FixtureSource, TestContext);

        result.Succeeded.ShouldBeTrue();
        result.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexProjectAsync_SwiftRepository_ReportsSwiftLanguageStatsAndSurvivesUnparsableFiles()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-swift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(rootPath, "Sources"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Shapes.swift"), FixtureSource);
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "Sources", "Broken.swift"),
                "class {{{ func ]]] protocol : : @@@ }\n\u0000\u0001 extension\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("SwiftRepo", rootPath, ct: CancellationToken.None);

            store.Nodes.ShouldContain(node =>
                node.Name == "ShapeStore" && node.Label == NodeLabel.Class);
            store.Nodes.ShouldContain(node =>
                node.Name == "Shape" && node.Label == NodeLabel.Interface);

            var repo = await store.GetRepositoryByName("SwiftRepo");
            repo.ShouldNotBeNull();
            repo.Language.ShouldBe("Swift");
            var languageStats = (JsonElement)repo.Properties!["languageStats"];
            languageStats.TryGetProperty("Swift", out var swiftStats).ShouldBeTrue();
            swiftStats.GetProperty("files").GetInt32().ShouldBe(2);
            swiftStats.GetProperty("nodeCount").GetInt32().ShouldBeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void AssertType(ExtractionResult result, string name, NodeLabel label,
        string declarationKind, string qualifiedName, string parentQN)
    {
        var node = result.Nodes.Where(
            n => n.Name == name && n.QualifiedName == qualifiedName).ShouldHaveSingleItem();
        node.Label.ShouldBe(label);
        node.Properties["declaration_kind"].ShouldBe(declarationKind);
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == parentQN &&
            edge.TargetQN == qualifiedName &&
            edge.Type == EdgeType.DEFINES);
    }

    private static GraphNode AssertCallable(ExtractionResult result, string name,
        NodeLabel label, string parentQN)
    {
        var node = result.Nodes.Where(n =>
            n.Name == name &&
            n.QualifiedName.StartsWith(parentQN + "#", StringComparison.Ordinal) &&
            !n.QualifiedName[(parentQN.Length + 1)..].Contains('#')).ShouldHaveSingleItem();
        node.Label.ShouldBe(label);
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == parentQN &&
            edge.TargetQN == node.QualifiedName &&
            edge.Type == EdgeType.DEFINES_METHOD);
        return node;
    }
}
