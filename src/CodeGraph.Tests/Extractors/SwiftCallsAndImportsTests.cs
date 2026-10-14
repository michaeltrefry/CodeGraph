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
/// Swift calls (unresolved calls with receivers and argument labels, resolved by the pipeline to
/// exact CALLS edges) and imports (module records).
/// </summary>
public class SwiftCallsAndImportsTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static Task<ExtractionResult> ExtractAsync(string relativePath, string source) =>
        new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)
            .ExtractAsync($"/test/{relativePath}", source, TestContext);

    // ── Extractor: imports ──────────────────────────────────────────

    [Fact]
    public async Task Imports_RecordTheModule_AndTheSymbolOfAScopedImport()
    {
        var result = await ExtractAsync("App.swift", """
            import Foundation
            @testable import MyKit
            import struct Foundation.Date
            import class UIKit.UIView
            import func Darwin.sqrt
            import Darwin.C
            func main() {}
            """);

        const string file = "TestProject:App.swift";
        result.UnresolvedImports.ShouldBe(
        [
            new UnresolvedImport(file, "Foundation"),
            new UnresolvedImport(file, "MyKit"),
            new UnresolvedImport(file, "Foundation", "Date"),
            new UnresolvedImport(file, "UIKit", "UIView"),
            new UnresolvedImport(file, "Darwin", "sqrt"),
            new UnresolvedImport(file, "Darwin.C")
        ]);
    }

    // ── Extractor: call shapes ──────────────────────────────────────

    private const string P = "TestProject:Point.swift#type:Point";

    [Fact]
    public async Task Calls_CarryCalleeReceiverAndArgumentLabels()
    {
        var result = await ExtractAsync("Point.swift", """
            struct Point {
                var x: Double
                var origin: Point { Point(x: 0) }
                func moved(by d: Double) -> Point { self }
                func run(_ other: Point?, items: [Point]) {
                    foo()
                    obj.method()
                    self.moved(by: 1)
                    Point.zero()
                    a.b.c()
                    items.map { $0 }
                    other?.moved(by: 2)
                    Point(x: 1)
                    Point.init(x: 2)
                    moved(by: 3)
                    task { } onDone: { }
                    let p = Point(x: 4)
                    p.origin.moved(by: 5)
                    let q: Point = .init(x: 6)
                    for item in items { item.moved(by: 7) }
                }
            }
            """);

        const string run = $"{P}#method:run(_other:Point?,items:[Point])";
        result.UnresolvedCalls.Where(call => call.CallerQN == run)
            .Select(call => (call.CalleeName, call.ReceiverType, call.ReceiverKind, call.ArgumentLabels))
            .ShouldBe(
            [
                ("foo", null, CallReceiverKind.Bare, ""),
                ("method", null, CallReceiverKind.Unresolved, ""),
                ("moved", P, CallReceiverKind.Resolved, "by:"),
                ("zero", P, CallReceiverKind.Resolved, ""),
                ("c", null, CallReceiverKind.Unresolved, ""),
                ("map", null, CallReceiverKind.Unresolved, "*:"),
                ("moved", P, CallReceiverKind.Resolved, "by:"),
                ("init", P, CallReceiverKind.Resolved, "x:"),
                ("init", P, CallReceiverKind.Resolved, "x:"),
                ("moved", P, CallReceiverKind.Resolved, "by:"),
                ("task", null, CallReceiverKind.Bare, "*:onDone:"),
                ("init", P, CallReceiverKind.Resolved, "x:"),
                ("moved", P, CallReceiverKind.Resolved, "by:"),
                ("init", P, CallReceiverKind.Resolved, "x:"),
                ("moved", null, CallReceiverKind.Unresolved, "by:")
            ]);

        // Accessor bodies own their calls.
        result.UnresolvedCalls.ShouldContain(new UnresolvedCall(
            $"{P}#property:origin", "init", P, 0.65, CallReceiverKind.Resolved, "x:"));
    }

    [Fact]
    public async Task TypesNestedInExtensions_AreReceiversUnderTheQualifiedNameTheWalkerEmits()
    {
        var result = await ExtractAsync("Nested.swift", """
            extension Shape {
                struct Style {
                    static func bold() {}
                    func use() { Style.bold() }
                }
                func paint() { Style.bold(); Self.Style.bold() }
            }
            struct Shape {}
            extension Remote {
                struct Tag { static func make() {} }
                func tag() { Tag.make() }
            }
            """);

        const string file = "TestProject:Nested.swift";
        result.Nodes.ShouldContain(node => node.QualifiedName == $"{file}#type:Shape#type:Style");
        result.Nodes.ShouldContain(node => node.QualifiedName == $"{file}#extension:Remote#type:Tag");

        string?[] Receivers(string callee) => result.UnresolvedCalls
            .Where(call => call.CalleeName == callee).Select(call => call.ReceiverType).ToArray();

        Receivers("bold").ShouldBe(
        [
            $"{file}#type:Shape#type:Style",
            $"{file}#type:Shape#type:Style",
            $"{file}#type:Shape#type:Style"
        ]);
        Receivers("make").ShouldBe([$"{file}#extension:Remote#type:Tag"]);
    }

    [Fact]
    public async Task CyclicDeclarations_LeaveTheReceiverUnresolved()
    {
        var result = await ExtractAsync("Cycle.swift", """
            final class Loop {
                var a = b.next()
                var b = a.next()
                func go() { a.step(); b.step() }
            }
            """);

        result.Succeeded.ShouldBeTrue();
        result.UnresolvedCalls.Where(call => call.CalleeName == "step")
            .Select(call => (call.ReceiverType, call.ReceiverKind))
            .ShouldBe([(null, CallReceiverKind.Unresolved), (null, CallReceiverKind.Unresolved)]);
    }

    [Fact]
    public async Task Declarations_RecordArgumentLabelSignatures()
    {
        var result = await ExtractAsync("Labels.swift", """
            struct Box {
                init() {}
                init(width w: Double = 1, _ height: Double) {}
                func add(_ values: Int..., to target: Int) {}
                subscript(index: Int) -> Int { index }
            }
            """);

        string Labels(string name, string parameters) =>
            result.Nodes.Single(node => node.QualifiedName == $"TestProject:Labels.swift#type:Box#method:{name}{parameters}")
                .Properties.GetValueOrDefault(ArgumentLabels.Property)?.ToString() ?? "<none>";

        Labels("init", "()").ShouldBe("");
        Labels("init", "(widthw:Double,_height:Double)").ShouldBe("width=:_:");
        Labels("add", "(_values:Int...,totarget:Int)").ShouldBe("_...:to:");
        Labels("subscript", "(index:Int)").ShouldBe("<none>");
    }

    [Theory]
    [InlineData("radius:", "radius:", true)]
    [InlineData("radius:", "", false)]
    [InlineData("", "radius:", false)]
    [InlineData("", "", true)]
    [InlineData("width=:_:", "_:", true)]
    [InlineData("width=:_:", "width:_:", true)]
    [InlineData("width=:_:", "height:", false)]
    [InlineData("_...:to:", "_:_:_:to:", true)]
    [InlineData("_...:to:", "to:", true)]
    [InlineData("_:", "*:", true)]
    [InlineData("named:_:", "*:", false)]
    [InlineData("_:onDone:", "*:onDone:", true)]
    public void ArgumentLabels_Accepts(string declared, string call, bool expected) =>
        ArgumentLabels.Accepts(declared, call).ShouldBe(expected);

    // ── Pipeline: exact CALLS edges ─────────────────────────────────

    private const string Project = "SwiftCalls";

    private const string ShapesSource = """
        struct Circle {
            let radius: Double
            init(radius: Double) { self.radius = radius }
            init() { self.init(radius: 1) }
            func area() -> Double { radius * radius * 3.14 }
            static func unit() -> Circle { Circle() }
            func styled() { _ = Style.bold() }
        }

        extension Circle {
            struct Style {
                static func bold() -> Int { 1 }
            }
        }

        final class Renderer {
            func draw(_ circle: Circle) {}
            func draw(_ circle: Circle, scale: Double) {}
            func batch(_ body: () -> Void) {}
            func batch(named: String, _ body: () -> Void) {}
            func flush() {}
        }

        struct Stack {
            mutating func append(_ value: Int) {}
        }

        func log(_ message: String) {}
        """;

    private const string ScaleSource = """
        extension Circle {
            func scaled(by factor: Double) -> Circle { Circle(radius: radius * factor) }
        }
        """;

    private const string SceneSource = """
        import Foundation
        @testable import Shapes

        final class Scene {
            let renderer: Renderer
            init(renderer: Renderer) { self.renderer = renderer }

            func render() {
                let circle = Circle(radius: 2)
                renderer.draw(circle)
                self.renderer.draw(circle, scale: 2)
                _ = circle.area()
                _ = Circle.unit()
                log("rendered")
                reset()
                let other: Circle = .init()
                helper(other)
                _ = circle.scaled(by: 2)
                renderer.batch { }
                let scene = self
                scene.renderer.flush()
                var values: [Int] = []
                values.append(1)
            }

            func reset() {}
        }

        func helper(_ c: Circle?) { _ = c?.area() }
        """;

    private const string S = $"{Project}:Sources/Shapes.swift";
    private const string X = $"{Project}:Sources/Circle+Scale.swift";
    private const string C = $"{Project}:Sources/Scene.swift";
    private const string Circle = $"{S}#type:Circle";
    private const string Renderer = $"{S}#type:Renderer";
    private const string Render = $"{C}#type:Scene#method:render()";

    private static readonly (string Source, string Target)[] ExpectedCalls =
    [
        ($"{Circle}#method:init()", $"{Circle}#method:init(radius:Double)"),
        ($"{Circle}#method:unit()", $"{Circle}#method:init()"),
        ($"{Circle}#method:styled()", $"{Circle}#type:Style#method:bold()"),
        ($"{X}#extension:Circle#method:scaled(byfactor:Double)", $"{Circle}#method:init(radius:Double)"),
        (Render, $"{Circle}#method:init(radius:Double)"),
        (Render, $"{Renderer}#method:draw(_circle:Circle)"),
        (Render, $"{Renderer}#method:draw(_circle:Circle,scale:Double)"),
        (Render, $"{Circle}#method:area()"),
        (Render, $"{Circle}#method:unit()"),
        (Render, $"{S}#function:log(_message:String)"),
        (Render, $"{C}#type:Scene#method:reset()"),
        (Render, $"{Circle}#method:init()"),
        (Render, $"{C}#function:helper(_c:Circle?)"),
        (Render, $"{X}#extension:Circle#method:scaled(byfactor:Double)"),
        (Render, $"{Renderer}#method:batch(_body:()->Void)"),
        (Render, $"{Renderer}#method:flush()"),
        ($"{C}#function:helper(_c:Circle?)", $"{Circle}#method:area()")
    ];

    private static List<(string Source, string Target)> Calls(InMemoryGraphStore store)
    {
        var byId = store.Nodes.ToDictionary(node => node.Id, node => node.QualifiedName);
        return store.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (byId[edge.SourceId], byId[edge.TargetId]))
            .ToList();
    }

    private static Task IndexAsync(InMemoryGraphStore store, string root) =>
        new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexProjectAsync(Project, root, ct: CancellationToken.None);

    private static async Task WithRepoAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sources", "Shapes.swift"), ShapesSource);
            await File.WriteAllTextAsync(Path.Combine(root, "Sources", "Circle+Scale.swift"), ScaleSource);
            await File.WriteAllTextAsync(Path.Combine(root, "Sources", "Scene.swift"), SceneSource);
            await test(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public Task IndexProject_SwiftCallsResolveToExactSameRepoFunctions() =>
        WithRepoAsync(async root =>
        {
            var store = new InMemoryGraphStore();
            await IndexAsync(store, root);

            Calls(store).ShouldBe(ExpectedCalls, ignoreOrder: true);
        });

    [Fact]
    public Task IndexProject_IncrementalRunTouchingOnlyTheCaller_StillResolvesIntoUnchangedFiles() =>
        WithRepoAsync(async root =>
        {
            var store = new InMemoryGraphStore();
            await IndexAsync(store, root);

            await File.WriteAllTextAsync(Path.Combine(root, "Sources", "Scene.swift"),
                SceneSource + "\nfunc later() { log(\"later\") }\n");
            await IndexAsync(store, root);

            Calls(store).ShouldBe(
                ExpectedCalls.Append(($"{C}#function:later()", $"{S}#function:log(_message:String)")),
                ignoreOrder: true);
        });
}
