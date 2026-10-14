using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Calls resolved by name bind within the caller's language family and across real FFI
/// boundaries only (Swift → C, Swift → C-ABI Rust), never to a same-named function of an
/// unrelated language in the same repository.
/// </summary>
public sealed class CrossLanguageCallResolutionTests : IDisposable
{
    private const string Project = "Mixed";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-xlang-calls-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task IndexProject_CallsBindWithinTheLanguageFamilyAndAcrossFfiBoundariesOnly()
    {
        Write("App/Canvas.swift", """
            final class Canvas: NSView {
                func draw() {
                    convert(1)
                    items()
                    brush_alpha_bounds(2)
                    lw_catalog_open()
                    layout()
                }
                func layout() {}
            }
            final class CatalogService {
                func cast() {}
            }
            """);
        Write("scripts/tool.py", "def convert(x):\n    return x\n\ndef main():\n    convert(2)\n");
        Write("engine/src/lib.rs", """
            pub fn items() -> u32 { 1 }

            #[no_mangle]
            pub extern "C" fn lw_catalog_open() -> u32 { items() }

            fn check() { cast(); brush_alpha_bounds(3); }
            """);
        Write("native/brush.c", "int brush_alpha_bounds(int x) { return x; }\n");

        var store = new InMemoryGraphStore();
        await new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexProjectAsync(Project, _root, ct: CancellationToken.None);

        var byId = store.Nodes.ToDictionary(node => node.Id);
        var calls = store.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (byId[edge.SourceId].Name, byId[edge.SourceId].FilePath, byId[edge.TargetId].Name,
                byId[edge.TargetId].FilePath))
            .ToList();

        calls.ShouldBe(
        [
            // Swift → C, Swift → C-ABI Rust, Swift → Swift.
            ("draw", "App/Canvas.swift", "brush_alpha_bounds", "native/brush.c"),
            ("draw", "App/Canvas.swift", "lw_catalog_open", "engine/src/lib.rs"),
            ("draw", "App/Canvas.swift", "layout", "App/Canvas.swift"),
            // Same language is unchanged; Rust → C crosses its FFI boundary.
            ("main", "scripts/tool.py", "convert", "scripts/tool.py"),
            ("lw_catalog_open", "engine/src/lib.rs", "items", "engine/src/lib.rs"),
            ("check", "engine/src/lib.rs", "brush_alpha_bounds", "native/brush.c")
        ], ignoreOrder: true);
        store.Nodes.Single(node => node.Name == "lw_catalog_open").Properties["abi"].ShouldBe("C");
        store.Nodes.Single(node => node.Name == "items" && node.Label == NodeLabel.Function)
            .Properties.ContainsKey("abi").ShouldBeFalse();
    }

    [Theory]
    [InlineData("pub extern \"C\" fn lw_open() -> u32", true)]
    [InlineData("pub unsafe extern \"C-unwind\" fn lw_open()", true)]
    [InlineData("extern fn lw_open()", true)]
    [InlineData("pub extern \"Rust\" fn lw_open()", false)]
    [InlineData("pub fn lw_open() -> u32", false)]
    public void ExportsCAbi_ReadsTheScipSignatureOfASemanticRustNode(string signature, bool expected)
    {
        var node = new GraphNode
        {
            Project = Project,
            Label = NodeLabel.Function,
            Name = "lw_open",
            QualifiedName = "rust-analyzer cargo engine lw_open().",
            FilePath = "engine/src/lib.rs",
            Properties = new() { ["signature"] = signature }
        };

        CallLanguages.ExportsCAbi(node).ShouldBe(expected);
        CallLanguages.CanCall("swift", node).ShouldBe(expected);
        CallLanguages.CanCall("python", node).ShouldBeFalse();
    }

    [Fact]
    public void CanCall_NodesOfUnknownLanguageAreNotFiltered()
    {
        var stub = new GraphNode { Project = Project, Label = NodeLabel.Method, Name = "convert", QualifiedName = "convert" };
        CallLanguages.CanCall("swift", stub).ShouldBeTrue();
        CallLanguages.CanCall(null, new GraphNode
        {
            Project = Project, Label = NodeLabel.Function, Name = "convert", QualifiedName = "py", FilePath = "a.py"
        }).ShouldBeTrue();
        CallLanguages.CanCall("javascript", new GraphNode
        {
            Project = Project, Label = NodeLabel.Function, Name = "f", QualifiedName = "js", FilePath = "a.js"
        }).ShouldBeTrue();
    }
}
