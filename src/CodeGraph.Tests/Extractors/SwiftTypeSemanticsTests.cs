using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Overlays;
using CodeGraph.Data;
using Microsoft.Extensions.Logging;
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
    private const string Repository = "SwiftRepo";

    private static readonly ExtractorContext TestContext = new()
    {
        RepositoryName = "TestProject",
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
        ShouldHaveEdge(result, square, $"{square}#method:scaled(by:Double)", EdgeType.DEFINES_METHOD);
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
            edge.Properties!["origin_file"].ShouldBe("Sources/Square+Extras.swift");
        }

        result.Edges.Single(edge => edge.TargetQN == $"{f}#extension:Array#method:total()")
            .SourceQN.ShouldBe("swift-type:Array");
        // No type node is invented for the extended types.
        result.Nodes.ShouldNotContain(node => node.Name == "Square" || node.Name == "Array");
    }


    [Fact]
    public async Task UnqualifiedNames_ResolveLexically_NotToNestedTypesOfOtherScopes()
    {
        var result = await ExtractAsync("Scopes.swift", """
            enum Feature {
                struct View {}
                struct Panel: View {}
            }
            extension View {
                func card() {}
            }
            struct Home: View {}
            """);
        const string f = "TestProject:Scopes.swift";

        // Inside Feature, View is Feature.View.
        ShouldHaveEdge(result, $"{f}#type:Feature#type:Panel", $"{f}#type:Feature#type:View", EdgeType.IMPLEMENTS);
        // At file scope there is no top-level View in this file: the reference stays symbolic.
        ShouldHaveEdge(result, $"{f}#type:Home", "swift-type:View", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, "swift-type:View", $"{f}#extension:View#method:card()", EdgeType.DEFINES_METHOD);
        result.Edges.Where(edge => edge.TargetQN == $"{f}#type:Feature#type:View" && edge.Type != EdgeType.DEFINES)
            .Select(edge => edge.SourceQN)
            .ShouldBe([$"{f}#type:Feature#type:Panel"]);
    }

    [Fact]
    public async Task Class_DiffableDataSourceFirstEntry_IsASuperclass()
    {
        var result = await ExtractAsync("Lists.swift", """
            class Rows: UITableViewDiffableDataSource<Int, String>, UITableViewDataSource {}
            class Cells: UICollectionViewDiffableDataSource<Int, String> {}
            """);
        const string f = "TestProject:Lists.swift";

        ShouldHaveEdge(result, $"{f}#type:Rows", "swift-type:UITableViewDiffableDataSource", EdgeType.INHERITS);
        ShouldHaveEdge(result, $"{f}#type:Rows", "swift-type:UITableViewDataSource", EdgeType.IMPLEMENTS);
        ShouldHaveEdge(result, $"{f}#type:Cells", "swift-type:UICollectionViewDiffableDataSource", EdgeType.INHERITS);
    }

    // ── Pipeline: cross-file resolution ─────────────────────────────

    private const string ModelsSource = """
        protocol Describable {}
        class Base {}
        struct Square {
            let side: Double
        }
        """;

    private const string SquareDeclaration = "struct Square {\n    let side: Double\n}";

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

    private const string MQ = $"{Repository}:Sources/Models.swift";
    private const string EQ = $"{Repository}:Sources/Extras.swift";

    private static (string Source, string Target, EdgeType Type)[] SquareEdges(string square) =>
    [
        (square, $"{EQ}#extension:Square#method:init(unit:Void)", EdgeType.DEFINES_METHOD),
        (square, $"{EQ}#extension:Square#property:perimeter", EdgeType.DEFINES),
        (square, $"{EQ}#extension:Square#type:Tag", EdgeType.DEFINES),
        (square, $"{EQ}#extension:Square#method:doubled()", EdgeType.DEFINES_METHOD),
        (square, $"{MQ}#type:Describable", EdgeType.IMPLEMENTS)
    ];

    private static readonly (string Source, string Target, EdgeType Type)[] OtherExtrasEdges =
    [
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

    private static IEnumerable<(string Source, string Target, EdgeType Type)> BaselineEdges(string square) =>
        SquareEdges(square).Concat(OtherExtrasEdges);

    private static IndexingPipeline Pipeline(InMemoryGraphStore store, ILogger<IndexingPipeline>? logger = null) => new(
        store,
        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
        Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
        new LocalFileSystem(),
        logger ?? NullLogger<IndexingPipeline>.Instance);

    private sealed class RecordingLogger : ILogger<IndexingPipeline>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// The stored edges cross-file Swift resolution decides: every INHERITS/IMPLEMENTS edge, and
    /// every definition edge of a cross-file extension member or from a stub.
    /// </summary>
    private static List<(string Source, string Target, EdgeType Type)> SwiftRelations(InMemoryGraphStore store)
    {
        var byId = store.Nodes.ToDictionary(node => node.Id, node => node.QualifiedName);
        return store.Edges
            .Select(edge => (Source: byId[edge.SourceId], Target: byId[edge.TargetId], edge.Type))
            .Where(edge => edge.Type is EdgeType.INHERITS or EdgeType.IMPLEMENTS
                           || (edge.Type is EdgeType.DEFINES or EdgeType.DEFINES_METHOD
                               && (edge.Target.Contains("#extension:", StringComparison.Ordinal)
                                   || edge.Source.StartsWith("swift-external:", StringComparison.Ordinal))))
            .ToList();
    }

    private static void ShouldHaveExactly(
        InMemoryGraphStore store, IEnumerable<(string Source, string Target, EdgeType Type)> expected) =>
        SwiftRelations(store).ShouldBe(expected.ToList(), ignoreOrder: true);

    private static GraphEdge StoredEdge(Repo repo, string source, string target, EdgeType type)
    {
        var byId = repo.Store.Nodes.ToDictionary(node => node.Id, node => node.QualifiedName);
        return repo.Store.Edges.Single(edge =>
            byId[edge.SourceId] == source && byId[edge.TargetId] == target && edge.Type == type);
    }

    private static Dictionary<string, NodeLabel> Stubs(InMemoryGraphStore store) =>
        store.Nodes.Where(node => node.QualifiedName.StartsWith("swift-external:", StringComparison.Ordinal))
            .ToDictionary(node => node.QualifiedName, node => node.Label);

    private sealed class Repo(string root)
    {
        public InMemoryGraphStore Store { get; } = new();
        public string Root => root;

        public Repo Write(string relativePath, string source)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            return this;
        }

        public Repo Delete(string relativePath)
        {
            File.Delete(Path.Combine(root, relativePath));
            return this;
        }

        /// <summary>
        /// An incremental run: with <paramref name="changedFiles"/> a change-list run, otherwise a
        /// full scan that also detects files deleted since the last run.
        /// </summary>
        public ILogger<IndexingPipeline>? Logger { get; set; }

        public Task IndexAsync(params string[] changedFiles) =>
            Pipeline(Store, Logger).IndexRepositoryAsync(Repository, root,
                changedFilesOnly: changedFiles.Length == 0 ? null : changedFiles,
                ct: CancellationToken.None);

        public Task<BranchOverlaySlice> OverlayAsync(IReadOnlyCollection<string> changed, IReadOnlyCollection<string> removed) =>
            Pipeline(Store).ExtractOverlayAsync(Repository, root, changed, removed, [], CancellationToken.None);

        public long Id(string qualifiedName) => Store.Nodes.Single(node => node.QualifiedName == qualifiedName).Id;
    }

    private static async Task WithRepoAsync(Func<Repo, Task> test, bool baseline = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-types-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var repo = new Repo(root);
            if (baseline)
            {
                repo.Write("Sources/Models.swift", ModelsSource).Write("Sources/Extras.swift", ExtrasSource);
            }

            await test(repo);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public Task IndexRepository_CrossFileExtensionMembersAndRelationsResolveToTheRealTypes() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();

            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square"));
            Stubs(repo.Store).ShouldBe(new Dictionary<string, NodeLabel>
            {
                ["swift-external:ObservableObject"] = NodeLabel.Interface,
                ["swift-external:UIViewController"] = NodeLabel.Class,
                ["swift-external:Hashable"] = NodeLabel.Interface,
                ["swift-external:Array"] = NodeLabel.Class
            }, ignoreOrder: true);
            repo.Store.Nodes.ShouldNotContain(node => node.QualifiedName.StartsWith("swift-type:"));
        });

    [Fact]
    public Task IndexRepository_IncrementalRuns_KeepEveryBindingExact() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();

            // Only the type's file changes: members of the unchanged extension stay attached.
            await repo.Write("Sources/Models.swift", ModelsSource + "\nfunc helper() {}\n").IndexAsync();
            repo.Store.Nodes.ShouldContain(node => node.Name == "helper");
            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square"));

            // Only the extension's file changes: it resolves against the persisted type.
            await repo.Write("Sources/Extras.swift", ExtrasSource + "\nfunc other() {}\n").IndexAsync();
            repo.Store.Nodes.ShouldContain(node => node.Name == "other");
            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square"));

            // The type moves to another file: the members follow it.
            await repo.Write("Sources/Models.swift", ModelsSource.Replace(SquareDeclaration, ""))
                .Write("Sources/Square.swift", SquareDeclaration + "\n")
                .IndexAsync();
            ShouldHaveExactly(repo.Store, BaselineEdges($"{Repository}:Sources/Square.swift#type:Square"));

            // The type is deleted: its members and conformance fall back to an external stub.
            await repo.Delete("Sources/Square.swift").IndexAsync("Sources/Square.swift");
            ShouldHaveExactly(repo.Store, BaselineEdges("swift-external:Square"));
            Stubs(repo.Store)["swift-external:Square"].ShouldBe(NodeLabel.Class);

            // It comes back, then is deleted again and noticed by a routine full scan
            // (no change list): the same fallback as a reported deletion.
            await repo.Write("Sources/Square.swift", SquareDeclaration + "\n").IndexAsync();
            ShouldHaveExactly(repo.Store, BaselineEdges($"{Repository}:Sources/Square.swift#type:Square"));
            await repo.Delete("Sources/Square.swift").IndexAsync();
            ShouldHaveExactly(repo.Store, BaselineEdges("swift-external:Square"));
            Stubs(repo.Store)["swift-external:Square"].ShouldBe(NodeLabel.Class);

            // It comes back: bound again, and the stub is gone.
            await repo.Write("Sources/Square.swift", SquareDeclaration + "\n").IndexAsync();
            ShouldHaveExactly(repo.Store, BaselineEdges($"{Repository}:Sources/Square.swift#type:Square"));
            Stubs(repo.Store).ShouldNotContainKey("swift-external:Square");
        });

    [Fact]
    public Task IndexRepository_ExtensionRemovedFromItsFile_TakesItsEdgesAndOrphanedStubAlong() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();

            // The Array extension's edges join two nodes outside Extras.swift (a stub and Models'
            // Describable); they still belong to Extras.swift and go when it stops declaring them.
            var withoutArray = ExtrasSource[..ExtrasSource.IndexOf("extension Array", StringComparison.Ordinal)];
            await repo.Write("Sources/Extras.swift", withoutArray).IndexAsync();

            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square")
                .Where(edge => edge.Source != "swift-external:Array"));
            Stubs(repo.Store).ShouldNotContainKey("swift-external:Array");
        });

    [Fact]
    public Task IndexRepository_TypeAddedLater_RebindsStubMembersAndRemovesTheStub() =>
        WithRepoAsync(async repo =>
        {
            await repo.Write("Sources/Ext.swift", "extension Square {\n    func doubled() {}\n}\n").IndexAsync();
            const string member = $"{Repository}:Sources/Ext.swift#extension:Square#method:doubled()";
            ShouldHaveExactly(repo.Store, [("swift-external:Square", member, EdgeType.DEFINES_METHOD)]);

            await repo.Write("Sources/Square.swift", "struct Square {}\n").IndexAsync();

            ShouldHaveExactly(repo.Store,
                [($"{Repository}:Sources/Square.swift#type:Square", member, EdgeType.DEFINES_METHOD)]);
            repo.Store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external:Square");
        }, baseline: false);

    [Fact]
    public Task IndexRepository_SupertypeDeclaredLater_TakesItsKind_AndTheGuessReturnsWhenItGoes() =>
        WithRepoAsync(async repo =>
        {
            const string sub = $"{Repository}:Sources/Sub.swift#type:Sub";
            await repo.Write("Sources/Sub.swift", "class Sub: Base {}\n").IndexAsync();
            ShouldHaveExactly(repo.Store, [(sub, "swift-external:Base", EdgeType.INHERITS)]);

            // Base turns out to be a protocol declared in the repository.
            await repo.Write("Sources/Base.swift", "protocol Base {}\n").IndexAsync();
            ShouldHaveExactly(repo.Store, [(sub, $"{Repository}:Sources/Base.swift#type:Base", EdgeType.IMPLEMENTS)]);
            repo.Store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external:Base");

            // Base leaves the repository again: back to the name-based guess.
            await repo.Delete("Sources/Base.swift").IndexAsync("Sources/Base.swift");
            ShouldHaveExactly(repo.Store, [(sub, "swift-external:Base", EdgeType.INHERITS)]);
            var byId = repo.Store.Nodes.ToDictionary(node => node.Id, node => node.QualifiedName);
            repo.Store.Edges.Single(edge => byId[edge.SourceId] == sub && edge.Type == EdgeType.INHERITS)
                .Properties["inheritance_inferred"].ShouldBe(true);
        }, baseline: false);

    [Fact]
    public Task IndexRepository_AmbiguityAppearsAndResolves_MembersMoveBetweenFileAndType() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();

            // A second top-level Square makes the extended type ambiguous.
            await repo.Write("Sources/Other.swift", "struct Square {}\n").IndexAsync();
            const string extras = $"{Repository}:Sources/Extras.swift";
            (string, string, EdgeType)[] fallback =
            [
                (extras, $"{EQ}#extension:Square#method:init(unit:Void)", EdgeType.DEFINES_METHOD),
                (extras, $"{EQ}#extension:Square#property:perimeter", EdgeType.DEFINES),
                (extras, $"{EQ}#extension:Square#type:Tag", EdgeType.DEFINES),
                (extras, $"{EQ}#extension:Square#method:doubled()", EdgeType.DEFINES_METHOD)
            ];
            // The conformance is recorded for every candidate, with low confidence.
            (string, string, EdgeType)[] candidateConformances =
            [
                ($"{MQ}#type:Square", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS),
                ($"{Repository}:Sources/Other.swift#type:Square", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS)
            ];
            ShouldHaveExactly(repo.Store, fallback.Concat(candidateConformances).Concat(OtherExtrasEdges));
            foreach (var (source, target, type) in candidateConformances)
            {
                var properties = StoredEdge(repo, source, target, type).Properties;
                properties["swift_ambiguous"].ShouldBe(true);
                properties["confidence"].ShouldBe("low");
            }

            // It goes away: the members bind to the remaining type again.
            await repo.Delete("Sources/Other.swift").IndexAsync("Sources/Other.swift");
            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square"));
            var conformance = StoredEdge(repo, $"{MQ}#type:Square", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS);
            conformance.Properties.ShouldNotContainKey("swift_ambiguous");
            conformance.Properties["confidence"].ShouldBe("high");
        });

    [Fact]
    public Task IndexRepository_AmbiguousNames_AreReportedAsAWarningListingAtMostTwentyPaths() =>
        WithRepoAsync(async repo =>
        {
            var names = Enumerable.Range(0, 22).Select(i => $"T{i:00}").ToList();
            var declarations = string.Concat(names.Select(name => $"struct {name} {{}}\n"));
            repo.Write("Sources/A.swift", declarations)
                .Write("Sources/B.swift", declarations)
                .Write("Sources/C.swift", $"struct U: {string.Join(", ", names)} {{}}\n");
            var logger = new RecordingLogger();
            repo.Logger = logger;

            await repo.IndexAsync();

            var warning = logger.Entries
                .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("ambiguous"))
                .ShouldHaveSingleItem().Message;
            warning.ShouldContain("22 ambiguous");
            warning.ShouldEndWith(string.Join(", ", names.Take(20)));
            warning.ShouldNotContain("T20");
        }, baseline: false);

    [Fact]
    public Task IndexRepository_UnqualifiedNamesBindOnlyToTopLevelTypes() =>
        WithRepoAsync(async repo =>
        {
            await repo.Write("Sources/Feature.swift", "enum Feature {\n    struct View {}\n}\n")
                .Write("Sources/Card.swift", "extension View {\n    func card() {}\n}\n")
                .Write("Sources/Home.swift", "struct Home: View {}\n")
                .IndexAsync();

            ShouldHaveExactly(repo.Store,
            [
                ("swift-external:View", $"{Repository}:Sources/Card.swift#extension:View#method:card()", EdgeType.DEFINES_METHOD),
                ($"{Repository}:Sources/Home.swift#type:Home", "swift-external:View", EdgeType.IMPLEMENTS)
            ]);
            Stubs(repo.Store)["swift-external:View"].ShouldBe(NodeLabel.Interface);
        }, baseline: false);

    [Fact]
    public Task IndexRepository_QualifiedProtocolAndConstrainedExtensions_BindExactly() =>
        WithRepoAsync(async repo =>
        {
            const string outer = $"{Repository}:Sources/Outer.swift";
            await repo.Write("Sources/Outer.swift", """
                    protocol Describable {}
                    protocol Greeter {
                        func greet()
                    }
                    enum Outer {
                        struct Inner {}
                    }
                    """)
                .Write("Sources/Inner.swift", "struct Inner {}\n")
                .Write("Sources/Extensions.swift", """
                    extension Outer.Inner: Describable {
                        func label() -> String { "" }
                    }
                    extension Greeter {
                        func greet() {}
                        var tag: String { "" }
                    }
                    extension Array: Describable where Element: Greeter {
                        func describeAll() {}
                    }
                    """)
                .IndexAsync();

            const string ext = $"{Repository}:Sources/Extensions.swift";
            ShouldHaveExactly(repo.Store,
            [
                ($"{outer}#type:Outer#type:Inner", $"{ext}#extension:Outer.Inner#method:label()", EdgeType.DEFINES_METHOD),
                ($"{outer}#type:Outer#type:Inner", $"{outer}#type:Describable", EdgeType.IMPLEMENTS),
                ($"{outer}#type:Greeter", $"{ext}#extension:Greeter#method:greet()", EdgeType.DEFINES_METHOD),
                ($"{outer}#type:Greeter", $"{ext}#extension:Greeter#property:tag", EdgeType.DEFINES),
                ("swift-external:Array", $"{ext}#extension:Array#method:describeAll()", EdgeType.DEFINES_METHOD),
                ("swift-external:Array", $"{outer}#type:Describable", EdgeType.IMPLEMENTS)
            ]);
        }, baseline: false);

    [Fact]
    public Task IndexRepository_IncrementalRun_UsesBatchedLookups() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();
            repo.Store.LookupCallCounts.Clear();

            await repo.Write("Sources/Models.swift", ModelsSource + "\n// 1\n")
                .Write("Sources/Extras.swift", ExtrasSource + "\n// 2\n")
                .Write("Sources/More.swift", "struct More: Describable {}\n")
                .IndexAsync();

            repo.Store.LookupCallCounts.ShouldBe(new Dictionary<string, int>
            {
                ["FindNodesByFilesAsync"] = 1,
                ["FindNodesByNamesAsync"] = 1,
                ["FindEdgesByPropertyAsync"] = 3
            }, ignoreOrder: true);
            ShouldHaveExactly(repo.Store, BaselineEdges($"{MQ}#type:Square")
                .Append(($"{Repository}:Sources/More.swift#type:More", $"{MQ}#type:Describable", EdgeType.IMPLEMENTS)));
        });

    // ── Overlays ────────────────────────────────────────────────────

    private static Task<OverlayGraphView> ViewAsync(
        Repo repo, BranchOverlaySlice slice, params (string Path, string Kind)[] files) =>
        OverlayGraphView.CreateAsync(
            repo.Store,
            new BranchOverlayEntity { Id = 1, Repository = Repository, HeadSha = "head", BaseSha = "base" },
            files.Select(file => new BranchOverlayFileEntity
            {
                OverlayId = 1, FilePath = file.Path, ChangeKind = file.Kind
            }).ToList(),
            slice.Nodes,
            slice.Edges);

    [Fact]
    public Task ExtractOverlay_ChangedTypeFile_CarriesUnchangedExtensionMembers() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();

            repo.Write("Sources/Models.swift", ModelsSource + "\nfunc helper() {}\n");
            var slice = await repo.OverlayAsync(["Sources/Models.swift"], []);

            foreach (var (source, target, type) in SquareEdges($"{MQ}#type:Square"))
                slice.Edges.ShouldContain(edge =>
                    edge.SourceQN == source && edge.TargetQN == target && edge.Type == type);
            slice.Edges.ShouldNotContain(edge => edge.Properties != null
                                                 && edge.Properties.ContainsKey(OverlayGraphView.RetractedProperty));
        });

    [Fact]
    public Task ExtractOverlay_TypeAddedOnBranch_RebindsStubMembersInTheView() =>
        WithRepoAsync(async repo =>
        {
            await repo.Write("Sources/Ext.swift", "extension Square {\n    func doubled() {}\n}\n").IndexAsync();
            const string member = $"{Repository}:Sources/Ext.swift#extension:Square#method:doubled()";
            const string square = $"{Repository}:Sources/Square.swift#type:Square";
            var memberId = repo.Id(member);

            repo.Write("Sources/Square.swift", "struct Square {}\n");
            var slice = await repo.OverlayAsync(["Sources/Square.swift"], []);

            slice.Edges.ShouldContain(edge =>
                edge.SourceQN == square && edge.TargetQN == member && edge.Type == EdgeType.DEFINES_METHOD);
            slice.Edges.Where(edge => edge.Properties != null
                                      && edge.Properties.ContainsKey(OverlayGraphView.RetractedProperty))
                .Select(edge => (edge.SourceQN, edge.TargetQN, edge.Type))
                .ShouldBe([("swift-external:Square", member, EdgeType.DEFINES_METHOD)]);

            var view = await ViewAsync(repo, slice, ("Sources/Square.swift", BranchOverlayChangeKinds.Added));
            var owners = await view.FindEdgesByTargetAsync(memberId, EdgeType.DEFINES_METHOD);
            var ownerNodes = await view.FindNodesByIdBatchAsync(owners.Select(edge => edge.SourceId).ToList());
            ownerNodes.Values.Select(node => node.QualifiedName).ShouldBe([square]);
            (await view.FindNodeByQualifiedNameAsync(Repository, "swift-external:Square")).ShouldBeNull();
        }, baseline: false);

    [Fact]
    public Task ExtractOverlay_SecondTypeOnBranch_HidesTheDefaultBranchOwnerEdge() =>
        WithRepoAsync(async repo =>
        {
            await repo.IndexAsync();
            const string member = $"{EQ}#extension:Square#method:doubled()";
            var memberId = repo.Id(member);

            // A second Square on the branch makes the extended type ambiguous: the member's
            // default-branch owner edge (between two unchanged nodes) must disappear at the head.
            repo.Write("Sources/Other.swift", "struct Square {}\n");
            var slice = await repo.OverlayAsync(["Sources/Other.swift"], []);

            var view = await ViewAsync(repo, slice, ("Sources/Other.swift", BranchOverlayChangeKinds.Added));
            var owners = await view.FindEdgesByTargetAsync(memberId, EdgeType.DEFINES_METHOD);
            var ownerNodes = await view.FindNodesByIdBatchAsync(owners.Select(edge => edge.SourceId).ToList());
            ownerNodes.Values.Select(node => node.QualifiedName).ShouldBe([$"{Repository}:Sources/Extras.swift"]);
        });

    [Fact]
    public Task ExtractOverlay_NewExternalSupertype_StubTravelsWithTheSlice() =>
        WithRepoAsync(async repo =>
        {
            await repo.Write("Sources/App.swift", "struct App {}\n").IndexAsync();

            repo.Write("Sources/Model.swift", "final class Model: ObservableObject {}\n");
            var slice = await repo.OverlayAsync(["Sources/Model.swift"], []);

            slice.Nodes.ShouldContain(node =>
                node.QualifiedName == "swift-external:ObservableObject" && node.Label == NodeLabel.Interface);
            var view = await ViewAsync(repo, slice, ("Sources/Model.swift", BranchOverlayChangeKinds.Added));
            var model = await view.FindNodeByQualifiedNameAsync(Repository, $"{Repository}:Sources/Model.swift#type:Model");
            model.ShouldNotBeNull();
            var implemented = await view.FindEdgesBySourceAsync(model.Id, EdgeType.IMPLEMENTS);
            var targets = await view.FindNodesByIdBatchAsync(implemented.Select(edge => edge.TargetId).ToList());
            targets.Values.Select(node => node.QualifiedName).ShouldBe(["swift-external:ObservableObject"]);
        }, baseline: false);
}
