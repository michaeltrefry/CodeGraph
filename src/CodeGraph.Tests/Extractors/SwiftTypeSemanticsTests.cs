using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// Swift type semantics: extensions (same file and cross-file), inheritance and conformance
/// edges, and members (initializers, computed properties, nested types) under their type.
/// </summary>
public class SwiftTypeSemanticsTests
{
    private const string Project = "SwiftRepo";

    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static Task<ExtractionResult> ExtractAsync(string relativePath, string source) =>
        new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)
            .ExtractAsync($"/test/{relativePath}", source, TestContext);

    private static void ShouldHaveEdge(ExtractionResult result, string source, string target, EdgeType type) =>
        result.Edges.ShouldContain(
            edge => edge.SourceQN == source && edge.TargetQN == target && edge.Type == type,
            $"expected {type} {source} -> {target}");

    // ── Extractor: inheritance clauses ──────────────────────────────

    private const string Hierarchy = """
        protocol Named {}
        protocol Titled: Named, AnyObject {}
        class Base {}
        class Child: Base, Named, Titled {}
        class Conformer: Named {}
        struct Point: Named, Hashable {}
        enum Mode: String, CaseIterable, Named { case on }
        enum Shade: Named { case dark }
        actor Worker: Named {}
        """;

    private const string H = "TestProject:Hierarchy.swift";

    [Fact]
    public async Task Class_FirstEntryClassIsInherits_LaterEntriesImplement()
    {
        var result = await ExtractAsync("Hierarchy.swift", Hierarchy);

        ShouldHaveEdge(result, $"{H}#type:Child", $"{H}#type:Base", EdgeType.INHERITS);
        ShouldHaveEdge(result, $"{H}#type:Child", $"{H}#type:Named", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Child", $"{H}#type:Titled", EdgeType.IMPLEMENTS);
        result.Edges.Where(edge => edge.SourceQN == $"{H}#type:Child"
                                   && edge.Type is EdgeType.INHERITS or EdgeType.IMPLEMENTS)
            .Count().ShouldBe(3);
    }

    [Fact]
    public async Task Class_FirstEntryKnownProtocolIsImplements()
    {
        var result = await ExtractAsync("Hierarchy.swift", Hierarchy);

        ShouldHaveEdge(result, $"{H}#type:Conformer", $"{H}#type:Named", EdgeType.IMPLEMENTS);
        result.Edges.ShouldNotContain(edge =>
            edge.SourceQN == $"{H}#type:Conformer" && edge.Type == EdgeType.INHERITS);
    }

    [Fact]
    public async Task Protocol_RefinesProtocolsWithInherits_AndAnyObjectIsAClassBound()
    {
        var result = await ExtractAsync("Hierarchy.swift", Hierarchy);

        ShouldHaveEdge(result, $"{H}#type:Titled", $"{H}#type:Named", EdgeType.INHERITS);
        result.Edges.ShouldNotContain(edge => edge.TargetQN.Contains("AnyObject"));
        result.Nodes.Single(node => node.QualifiedName == $"{H}#type:Titled")
            .Properties["class_bound"].ShouldBe(true);
    }

    [Fact]
    public async Task StructEnumActor_ConformancesAreImplements_AndEnumRawTypeIsAProperty()
    {
        var result = await ExtractAsync("Hierarchy.swift", Hierarchy);

        ShouldHaveEdge(result, $"{H}#type:Point", $"{H}#type:Named", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Point", "swift-type:Hashable", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Mode", "swift-type:CaseIterable", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Mode", $"{H}#type:Named", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Shade", $"{H}#type:Named", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{H}#type:Worker", $"{H}#type:Named", EdgeType.IMPLEMENTS);

        result.Edges.ShouldNotContain(edge => edge.TargetQN.EndsWith("String"));
        result.Nodes.Single(node => node.QualifiedName == $"{H}#type:Mode")
            .Properties["raw_type"].ShouldBe("String");
        result.Edges.ShouldNotContain(edge =>
            edge.Type == EdgeType.INHERITS && edge.SourceQN != $"{H}#type:Child"
                                           && edge.SourceQN != $"{H}#type:Titled");
    }

    [Fact]
    public async Task Class_UnknownFirstEntry_IsInferredByName()
    {
        var result = await ExtractAsync("Views.swift", """
            final class Model: ObservableObject {}
            class Controller: UIViewController, UITableViewDelegate {}
            class Handler: CacheDelegate {}
            class Store<T: Equatable>: Container<T>, Foundation.Codable where T: Hashable {}
            """);
        const string f = "TestProject:Views.swift";

        var model = result.Edges.Single(edge => edge.SourceQN == $"{f}#type:Model");
        model.TargetQN.ShouldBe("swift-type:ObservableObject");
        model.Type.ShouldBe(EdgeType.IMPLEMENTS);
        model.Properties!["inheritance_inferred"].ShouldBe(true);

        var superclass = result.Edges.Single(edge =>
            edge.SourceQN == $"{f}#type:Controller" && edge.TargetQN == "swift-type:UIViewController");
        superclass.Type.ShouldBe(EdgeType.INHERITS);
        superclass.Properties!["inheritance_inferred"].ShouldBe(true);
        ShouldHaveEdge(result, $"{f}#type:Controller", "swift-type:UITableViewDelegate", EdgeType.IMPLEMENTS);

        ShouldHaveEdge(result, $"{f}#type:Handler", "swift-type:CacheDelegate", EdgeType.IMPLEMENTS);

        // Generic arguments are dropped; module qualification is kept.
        ShouldHaveEdge(result, $"{f}#type:Store", "swift-type:Container", EdgeType.INHERITS);
        ShouldHaveEdge(result, $"{f}#type:Store", "swift-type:Foundation.Codable", EdgeType.IMPLEMENTS);
    }

    // ── Extractor: members ──────────────────────────────────────────

    private const string Members = """
        protocol Shape {
            var area: Double { get }
            var label: String { get set }
            static var unit: String { get }
        }

        struct Square: Shape {
            let side: Double
            var observed: Int = 0 {
                didSet { print(observed) }
            }
            lazy var cache: [Int] = []

            init(side: Double) { self.side = side }

            var area: Double { side * side }

            var label: String {
                get { "square" }
                set { print(newValue) }
            }

            static var unit: String { return "cm" }

            struct Corner {
                var index: Int { 0 }
            }

            func describe() -> String {
                var local: Int { 1 }
                return label
            }
        }

        extension Square: CustomStringConvertible {
            init() { self.init(side: 1) }
            var description: String { label }
            enum Kind { case small }
            func scaled(by factor: Double) -> Square { Square(side: side * factor) }
        }
        """;

    private const string M = "TestProject:Members.swift";

    [Fact]
    public async Task ComputedProperties_AreNodesUnderTheirType_StoredPropertiesAreNot()
    {
        var result = await ExtractAsync("Members.swift", Members);

        var properties = result.Nodes.Where(node => node.Label == NodeLabel.Property)
            .Select(node => node.QualifiedName).ToList();
        properties.ShouldBe(
        [
            $"{M}#type:Shape#property:area",
            $"{M}#type:Shape#property:label",
            $"{M}#type:Shape#property:unit",
            $"{M}#type:Square#property:area",
            $"{M}#type:Square#property:label",
            $"{M}#type:Square#property:unit",
            $"{M}#type:Square#type:Corner#property:index",
            $"{M}#type:Square#property:description"
        ], ignoreOrder: true);
        foreach (var qn in properties)
            ShouldHaveEdge(result, qn[..qn.LastIndexOf("#property:", StringComparison.Ordinal)], qn, EdgeType.DEFINES);

        var area = result.Nodes.Single(node => node.QualifiedName == $"{M}#type:Square#property:area");
        area.Name.ShouldBe("area");
        area.Properties["type"].ShouldBe("Double");
        area.Properties["computed"].ShouldBe(true);
        area.Properties["accessors"].ShouldBe("get");
        area.Properties["is_static"].ShouldBe(false);

        var label = result.Nodes.Single(node => node.QualifiedName == $"{M}#type:Square#property:label");
        label.Properties["accessors"].ShouldBe("get,set");
        result.Nodes.Single(node => node.QualifiedName == $"{M}#type:Square#property:unit")
            .Properties["is_static"].ShouldBe(true);

        var requirement = result.Nodes.Single(node => node.QualifiedName == $"{M}#type:Shape#property:label");
        requirement.Properties["requirement"].ShouldBe(true);
        requirement.Properties["accessors"].ShouldBe("get,set");
        requirement.Properties["type"].ShouldBe("String");
    }

    [Fact]
    public async Task SameFileExtension_InitPropertyNestedTypeAndConformanceAttachToTheType()
    {
        var result = await ExtractAsync("Members.swift", Members);
        const string square = $"{M}#type:Square";

        ShouldHaveEdge(result, square, $"{square}#method:init()", EdgeType.DEFINES_METHOD);
        ShouldHaveEdge(result, square, $"{square}#method:init(side:Double)", EdgeType.DEFINES_METHOD);
        ShouldHaveEdge(result, square, $"{square}#property:description", EdgeType.DEFINES);
        ShouldHaveEdge(result, square, $"{square}#type:Kind", EdgeType.DEFINES);
        ShouldHaveEdge(result, square, $"{square}#method:scaled(byfactor:Double)", EdgeType.DEFINES_METHOD);
        ShouldHaveEdge(result, square, $"{square}#type:Corner", EdgeType.DEFINES);
        ShouldHaveEdge(result, square, "swift-type:CustomStringConvertible", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, square, $"{M}#type:Shape", EdgeType.IMPLEMENTS);

        result.Nodes.Single(node => node.QualifiedName == $"{square}#type:Kind").Label.ShouldBe(NodeLabel.Enum);
        result.Nodes.ShouldNotContain(node => node.QualifiedName.Contains("#extension:"));
        result.Edges.ShouldNotContain(edge => edge.SourceQN.StartsWith("swift-type:"));
    }

    [Fact]
    public async Task CrossFileExtension_MembersAndConformancesStartAtASymbolicOwner()
    {
        var result = await ExtractAsync("Sources/Square+Extras.swift", """
            extension Geometry.Square: Describable {
                init(unit: Void) { self.init(side: 1) }
                var perimeter: Double { side * 4 }
                struct Tag {}
                func doubled() -> Square { self }
            }
            extension Array<Int> {
                func total() -> Int { reduce(0, +) }
            }
            """);
        const string f = "TestProject:Sources/Square+Extras.swift";
        const string owner = "swift-type:Geometry.Square";

        var expected = new (string Target, EdgeType Type)[]
        {
            ($"{f}#extension:Geometry.Square#method:init(unit:Void)", EdgeType.DEFINES_METHOD),
            ($"{f}#extension:Geometry.Square#property:perimeter", EdgeType.DEFINES),
            ($"{f}#extension:Geometry.Square#type:Tag", EdgeType.DEFINES),
            ($"{f}#extension:Geometry.Square#method:doubled()", EdgeType.DEFINES_METHOD),
            ("swift-type:Describable", EdgeType.IMPLEMENTS)
        };
        foreach (var (target, type) in expected)
        {
            var edge = result.Edges.Single(edge => edge.SourceQN == owner && edge.TargetQN == target);
            edge.Type.ShouldBe(type);
            edge.Properties!["owner_ref"].ShouldBe(owner);
            edge.Properties!["extension_file"].ShouldBe("Sources/Square+Extras.swift");
        }

        result.Edges.Single(edge => edge.TargetQN == $"{f}#extension:Array#method:total()")
            .SourceQN.ShouldBe("swift-type:Array");
        // No type node is invented for the extended types.
        result.Nodes.ShouldNotContain(node => node.Name == "Square" || node.Name == "Array");
    }

    // ── Pipeline: cross-file resolution ─────────────────────────────

    private const string ModelsSource = """
        protocol Describable {}
        class Base {}
        struct Square {
            let side: Double
        }
        """;

    private const string ExtrasSource = """
        extension Square: Describable {
            init(unit: Void) { self.init(side: 1) }
            var perimeter: Double { side * 4 }
            struct Tag {}
            func doubled() -> Square { self }
        }
        class Sub: Base, Describable {}
        class Impl: Describable {}
        final class Model: ObservableObject {}
        class Controller: UIViewController {}
        protocol Fancy: Describable, Hashable {}
        extension Array: Describable {
            func total() -> Int { count }
        }
        """;

    private const string MQ = $"{Project}:Sources/Models.swift";
    private const string EQ = $"{Project}:Sources/Extras.swift";

    private static readonly (string Source, string Target, EdgeType Type)[] ExpectedCrossFileEdges =
    [
        ($"{MQ}#type:Square", $"{EQ}#extension:Square#method:init(unit:Void)", EdgeType.DEFINES_METHOD),
        ($"{MQ}#type:Square", $"{EQ}#extension:Square#property:perimeter", EdgeType.DEFINES),
        ($"{MQ}#type:Square", $"{EQ}#extension:Square#type:Tag", EdgeType.DEFINES),
        ($"{MQ}#type:Square", $"{EQ}#extension:Square#method:doubled()", EdgeType.DEFINES_METHOD),
        ($"{MQ}#type:Square", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS),
        ($"{EQ}#type:Sub", $"{MQ}#type:Base", EdgeType.INHERITS),
        ($"{EQ}#type:Sub", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS),
        ($"{EQ}#type:Impl", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS),
        ($"{EQ}#type:Model", "swift-external:ObservableObject", EdgeType.IMPLEMENTS),
        ($"{EQ}#type:Controller", "swift-external:UIViewController", EdgeType.INHERITS),
        ($"{EQ}#type:Fancy", $"{MQ}#type:Describable", EdgeType.INHERITS),
        ($"{EQ}#type:Fancy", "swift-external:Hashable", EdgeType.INHERITS),
        ("swift-external:Array", $"{EQ}#extension:Array#method:total()", EdgeType.DEFINES_METHOD),
        ("swift-external:Array", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS)
    ];

    private static IndexingPipeline Pipeline(InMemoryGraphStore store) => new(
        store,
        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
        Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
        new LocalFileSystem(),
        NullLogger<IndexingPipeline>.Instance);

    private static List<(string Source, string Target, EdgeType Type)> StoredEdges(InMemoryGraphStore store)
    {
        var byId = store.Nodes.ToDictionary(node => node.Id, node => node.QualifiedName);
        return store.Edges
            .Select(edge => (byId[edge.SourceId], byId[edge.TargetId], edge.Type))
            .ToList();
    }

    private static void ShouldHaveStoredEdges(
        InMemoryGraphStore store, IEnumerable<(string Source, string Target, EdgeType Type)> expected)
    {
        var edges = StoredEdges(store);
        foreach (var edge in expected)
            edges.ShouldContain(edge, $"missing stored edge {edge}");
    }

    private static async Task WithRepoAsync(Func<string, InMemoryGraphStore, Task> test)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-swift-types-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(rootPath, "Sources"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Models.swift"), ModelsSource);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Extras.swift"), ExtrasSource);
            await test(rootPath, new InMemoryGraphStore());
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public Task IndexProject_CrossFileExtensionMembersAndRelationsResolveToTheRealTypes() =>
        WithRepoAsync(async (rootPath, store) =>
        {
            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);

            ShouldHaveStoredEdges(store, ExpectedCrossFileEdges);

            // Members attach to the type, not to the extension's file.
            var edges = StoredEdges(store);
            edges.ShouldNotContain(edge =>
                edge.Source == $"{Project}:Sources/Extras.swift"
                && edge.Target.StartsWith($"{EQ}#extension:Square#", StringComparison.Ordinal));
            // A guessed supertype kind is replaced once the name resolves to a repository type.
            edges.ShouldNotContain((
                $"{EQ}#type:Impl", $"{MQ}#type:Describable", EdgeType.INHERITS));

            // External supertypes and extended types are stubs with the kind they are used as.
            var stubs = store.Nodes.Where(node => node.QualifiedName.StartsWith("swift-external:"))
                .ToDictionary(node => node.QualifiedName, node => node.Label);
            stubs.ShouldBe(new Dictionary<string, NodeLabel>
            {
                ["swift-external:ObservableObject"] = NodeLabel.Interface,
                ["swift-external:UIViewController"] = NodeLabel.Class,
                ["swift-external:Hashable"] = NodeLabel.Interface,
                ["swift-external:Array"] = NodeLabel.Class
            }, ignoreOrder: true);
            store.Nodes.ShouldNotContain(node => node.QualifiedName.StartsWith("swift-type:"));
        });

    [Fact]
    public Task IndexProject_IncrementalRuns_KeepExtensionMembersAttached() =>
        WithRepoAsync(async (rootPath, store) =>
        {
            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);

            // Only the type's file changes: members of the unchanged extension stay attached.
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Models.swift"),
                ModelsSource + "\nfunc helper() {}\n");
            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);
            store.Nodes.ShouldContain(node => node.Name == "helper");
            ShouldHaveStoredEdges(store, ExpectedCrossFileEdges);

            // Only the extension's file changes: it resolves against the persisted type.
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Extras.swift"),
                ExtrasSource + "\nfunc other() {}\n");
            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);
            store.Nodes.ShouldContain(node => node.Name == "other");
            ShouldHaveStoredEdges(store, ExpectedCrossFileEdges);

            // The type moves to another file: the members follow it.
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Models.swift"),
                ModelsSource.Replace("struct Square {\n    let side: Double\n}", "", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Square.swift"),
                "struct Square {\n    let side: Double\n}\n");
            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);
            store.Nodes.ShouldNotContain(node => node.QualifiedName == $"{MQ}#type:Square");
            const string moved = $"{Project}:Sources/Square.swift#type:Square";
            ShouldHaveStoredEdges(store,
            [
                (moved, $"{EQ}#extension:Square#method:init(unit:Void)", EdgeType.DEFINES_METHOD),
                (moved, $"{EQ}#extension:Square#property:perimeter", EdgeType.DEFINES),
                (moved, $"{EQ}#extension:Square#type:Tag", EdgeType.DEFINES),
                (moved, $"{EQ}#extension:Square#method:doubled()", EdgeType.DEFINES_METHOD),
                (moved, $"{MQ}#type:Describable", EdgeType.IMPLEMENTS)
            ]);
        });

    [Fact]
    public Task IndexProject_AmbiguousExtendedType_KeepsMembersUnderTheExtensionFile() =>
        WithRepoAsync(async (rootPath, store) =>
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Other.swift"),
                "enum Outer {\n    struct Square {}\n}\nstruct Square {}\n");

            await Pipeline(store).IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);

            var edges = StoredEdges(store);
            edges.ShouldContain((
                $"{Project}:Sources/Extras.swift",
                $"{EQ}#extension:Square#method:doubled()",
                EdgeType.DEFINES_METHOD));
            edges.ShouldNotContain(edge =>
                edge.Target == $"{EQ}#extension:Square#method:doubled()"
                && edge.Source.EndsWith("#type:Square", StringComparison.Ordinal));
            // An ambiguous conformance source yields no edge rather than a guessed one.
            edges.ShouldNotContain(edge =>
                edge.Source.EndsWith("#type:Square", StringComparison.Ordinal)
                && edge.Type == EdgeType.IMPLEMENTS);
        });

    [Fact]
    public async Task ExtractOverlay_ChangedTypeFile_CarriesUnchangedExtensionMembers()
    {
        await WithRepoAsync(async (rootPath, store) =>
        {
            var pipeline = Pipeline(store);
            await pipeline.IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);

            await File.WriteAllTextAsync(Path.Combine(rootPath, "Sources", "Models.swift"),
                ModelsSource + "\nfunc helper() {}\n");
            var slice = await pipeline.ExtractOverlayAsync(
                Project, rootPath, ["Sources/Models.swift"], [], [], CancellationToken.None);

            foreach (var (source, target, type) in ExpectedCrossFileEdges.Where(edge => edge.Source == $"{MQ}#type:Square"))
                slice.Edges.ShouldContain(edge =>
                    edge.SourceQN == source && edge.TargetQN == target && edge.Type == type);
        });
    }
}
