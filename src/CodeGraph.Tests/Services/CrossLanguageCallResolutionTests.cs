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
    private const string Repository = "Mixed";
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
    public async Task IndexRepository_CallsBindWithinTheLanguageFamilyAndAcrossFfiBoundariesOnly()
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
            .IndexRepositoryAsync(Repository, _root, ct: CancellationToken.None);

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

    /// <summary>
    /// sc-26145 (LensWorks re-run): residual spurious cross-language CALLS after language-family
    /// scoping. A call never binds to a package (Python → <c>blake3</c> crate), a C call binds to
    /// a Rust function only when it exports a C ABI (C → nested Rust test fn <c>count</c>), and a
    /// Rust call binds to C functions only, never to a C type (Rust → C struct <c>range</c>).
    /// </summary>
    [Fact]
    public async Task IndexRepository_CallsNeverBindToPackagesOrNonCallableForeignDefinitions()
    {
        Write("Cargo.toml", "[package]\nname = \"engine\"\nversion = \"0.1.0\"\n\n[dependencies]\nblake3 = \"1\"\n");
        Write("scripts/digest.py", "def digest(data):\n    return blake3(data)\n");
        Write("src/lib.rs", """
            pub fn spans() -> u32 { range(1, 2) }

            #[cfg(test)]
            mod tests {
                #[test]
                fn counts() {
                    unsafe extern "C" fn count() -> usize { 0 }
                    let _n = count();
                }
            }
            """);
        Write("native/gennmtab.c", """
            struct range { int lo; int hi; };
            int total(void) { return count(); }
            """);

        var store = new InMemoryGraphStore();
        await new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                cargoManifestExtractor: new CargoManifestExtractor())
            .IndexRepositoryAsync(Repository, _root, ct: CancellationToken.None);

        // The fixture really has each tempting target.
        store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "blake3");
        store.Nodes.ShouldContain(node => node.Name == "count" && node.FilePath == "src/lib.rs");
        store.Nodes.ShouldContain(node => node.Name == "range" && node.FilePath == "native/gennmtab.c");

        var byId = store.Nodes.ToDictionary(node => node.Id);
        var calls = store.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (Caller: byId[edge.SourceId].Name, CallerFile: byId[edge.SourceId].FilePath,
                Callee: byId[edge.TargetId].Name, Label: byId[edge.TargetId].Label))
            .ToList();
        calls.Where(call => call.Callee is "blake3" or "count" or "range" && call.Caller is not "counts").ShouldBeEmpty();
        calls.ShouldNotContain(call => call.Caller == "total" && call.Callee == "count");
        calls.ShouldNotContain(call => call.Caller == "spans" && call.Callee == "range");
        // Same-language binding is unaffected.
        calls.ShouldContain(call => call.Caller == "counts" && call.Callee == "count");
    }

    [Theory]
    // Callables bind within their family and across FFI boundaries.
    [InlineData("rust", NodeLabel.Function, "native/a.c", true)]
    [InlineData("swift", NodeLabel.Function, "native/a.m", true)]
    [InlineData("swift", NodeLabel.Function, "native/a.cpp", true)]
    [InlineData("python", NodeLabel.Function, "a.py", true)]
    [InlineData("swift", NodeLabel.Method, "App/A.swift", true)]
    // Across a family boundary only a free function binds: never a method or constructor.
    [InlineData("swift", NodeLabel.Method, "native/a.m", false)]
    [InlineData("rust", NodeLabel.Method, "native/a.cpp", false)]
    [InlineData("rust", NodeLabel.Constructor, "native/a.cpp", false)]
    [InlineData("c", NodeLabel.Delegate, "src/lib.rs", false)]
    // Rust links to a C++ function by name only with C linkage (abi recorded by the extractor).
    [InlineData("rust", NodeLabel.Function, "native/a.cpp", false)]
    // A type is a constructor call target within its own family only.
    [InlineData("python", NodeLabel.Class, "a.py", true)]
    [InlineData("swift", NodeLabel.Struct, "App/A.swift", true)]
    [InlineData("rust", NodeLabel.Struct, "native/gennmtab.c", false)]
    [InlineData("rust", NodeLabel.Class, "native/a.cpp", false)]
    [InlineData("swift", NodeLabel.Struct, "native/a.h", false)]
    // Never a call target, whatever the language.
    [InlineData("python", NodeLabel.Package, "Cargo.toml", false)]
    [InlineData("python", NodeLabel.Package, null, false)]
    [InlineData("swift", NodeLabel.Module, "Package.swift", false)]
    [InlineData("javascript", NodeLabel.NuGetPackage, null, false)]
    [InlineData("c", NodeLabel.File, "native/a.c", false)]
    [InlineData("rust", NodeLabel.Repository, null, false)]
    [InlineData("python", NodeLabel.Namespace, "a.py", false)]
    [InlineData(null, NodeLabel.Package, "Cargo.toml", false)]
    public void IsCallTarget_RestrictsCandidatesToCallablesAndSameFamilyTypes(
        string? callerFamily, NodeLabel label, string? filePath, bool expected)
    {
        CallLanguages.IsCallTarget(callerFamily, new GraphNode
        {
            Repository = Repository, Label = label, Name = "x", QualifiedName = "x", FilePath = filePath!
        }).ShouldBe(expected);
    }

    /// <summary>
    /// Foreign code links to a Rust function by name only when its symbol is not mangled: an
    /// <c>extern "C" fn</c> without <c>#[no_mangle]</c> / <c>#[export_name]</c> is a callback passed
    /// by pointer (LensWorks: a nested test fn <c>count</c> registered with SQLite).
    /// </summary>
    [Theory]
    [InlineData("#[no_mangle]\npub extern \"C\" fn f() {}\n", "C")]
    [InlineData("#[unsafe(no_mangle)]\npub extern \"C\" fn f() {}\n", "C")]
    [InlineData("#[export_name = \"lw_f\"]\npub extern \"C-unwind\" fn f() {}\n", "C-unwind")]
    [InlineData("#[no_mangle]\n/// Opens it.\n#[inline]\npub unsafe extern fn f() {}\n", "C")]
    [InlineData("pub extern \"C\" fn f() {}\n", null)]
    [InlineData("#[inline]\npub unsafe extern \"C\" fn f() {}\n", null)]
    [InlineData("#[no_mangle]\nfn other() {}\npub extern \"C\" fn f() {}\n", null)]
    [InlineData("#[no_mangle]\npub fn f() {}\n", null)]
    [InlineData("fn outer() {\n    #[no_mangle]\n    extern \"C\" fn f() {}\n}\n", "C")]
    [InlineData("fn outer() {\n    unsafe extern \"C\" fn f() {}\n}\n", null)]
    public async Task RustAbi_IsRecordedOnlyForFunctionsExportedBySymbolName(string source, string? expectedAbi)
    {
        var result = await new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)
            .ExtractAsync("/r/src/lib.rs", source, new ExtractorContext { RepositoryName = Repository, RootPath = "/r" });

        var function = result.Nodes.Single(node => node.Name == "f");
        function.Properties.GetValueOrDefault(CallLanguages.AbiProperty).ShouldBe(expectedAbi);
        CallLanguages.CanCall("c", function).ShouldBe(expectedAbi is not null);
    }

    [Fact]
    public void Versions_AreBumpedForTheResolutionAndRustAbiChanges()
    {
        // Resolution of unchanged sources changed (pipeline) and Rust abi properties changed (spec).
        ExtractionVersionCatalog.PipelineVersion.ShouldBeGreaterThanOrEqualTo(4);
        LanguageSpecs.Rust.ExtractionVersion.ShouldBe(3);
        // C++ functions with C linkage record abi C (sc-26145).
        LanguageSpecs.Cpp.ExtractionVersion.ShouldBe(2);
    }

    /// <summary>
    /// sc-26145 (LensWorks final check): a bare Rust call of a local closure <c>find</c> bound to the
    /// only C++ member named <c>find</c>. Across a language-family boundary a call binds only as a
    /// bare call to a free function: never to a method, never through a receiver (<c>T::f()</c>),
    /// and Rust reaches a C++ function only when it has C linkage.
    /// </summary>
    [Fact]
    public async Task IndexRepository_FfiCallsBindOnlyBareCallsToExposedFreeFunctions()
    {
        Write("engine/src/catalog.rs", """
            pub fn migrate(rows: &[u32]) -> u32 {
                let find = |id: u32| rows.iter().position(|r| *r == id);
                let _ = find(1);
                let _ = rows.iter().find(|r| **r == 2);
                c_open();
                plain_cpp();
                cpp_linked();
                cpp_block_linked();
                Codec::encode(1);
                0
            }
            """);
        Write("native/IUTF8String.cpp", """
            namespace AdobeXMPCommon {
            class IUTF8StringProxy {
            public:
                unsigned long find(const char* s) const { return 0; }
            };
            }
            class Codec {
            public:
                static int encode(int x) { return x; }
            };
            int plain_cpp(void) { return 1; }
            extern "C" int cpp_linked(void) { return 2; }
            extern "C" {
            int cpp_block_linked(void) { return 3; }
            }
            """);
        Write("native/open.c", "int c_open(void) { return 0; }\n");
        Write("App/View.swift", """
            final class View {
                func draw() {
                    find(1)
                    plain_cpp()
                    c_open()
                    Codec.encode(2)
                }
            }
            """);

        var store = new InMemoryGraphStore();
        await new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexRepositoryAsync(Repository, _root, ct: CancellationToken.None);

        // The fixture really has each tempting target, and the linkage the extractor recorded.
        store.Nodes.ShouldContain(node => node.Name == "find" && node.Label == NodeLabel.Method);
        store.Nodes.ShouldContain(node => node.Name == "encode" && node.Label == NodeLabel.Method);
        store.Nodes.Single(node => node.Name == "cpp_linked").Properties[CallLanguages.AbiProperty].ShouldBe("C");
        store.Nodes.Single(node => node.Name == "cpp_block_linked").Properties[CallLanguages.AbiProperty].ShouldBe("C");
        store.Nodes.Single(node => node.Name == "plain_cpp").Properties.ContainsKey(CallLanguages.AbiProperty).ShouldBeFalse();

        var byId = store.Nodes.ToDictionary(node => node.Id);
        store.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (byId[edge.SourceId].Name, byId[edge.TargetId].Name, byId[edge.TargetId].FilePath))
            .ToList()
            .ShouldBe(
            [
                ("migrate", "c_open", "native/open.c"),
                ("migrate", "cpp_linked", "native/IUTF8String.cpp"),
                ("migrate", "cpp_block_linked", "native/IUTF8String.cpp"),
                // Swift's C++ interop calls a C++ free function of any linkage.
                ("draw", "plain_cpp", "native/IUTF8String.cpp"),
                ("draw", "c_open", "native/open.c")
            ], ignoreOrder: true);
    }

    /// <summary>
    /// A receiver call whose owner is proven (here symbolically, through <c>receiver_owner</c>)
    /// still never resolves across a language-family boundary; the same function called bare does.
    /// </summary>
    [Fact]
    public async Task ResolveCalls_AResolvedReceiverCallDoesNotCrossIntoAnotherLanguageFamily()
    {
        var buffer = new GraphBuffer();
        GraphNode Node(NodeLabel label, string name, string qualifiedName, string filePath,
            Dictionary<string, object>? properties = null) => new()
        {
            Repository = Repository, Label = label, Name = name, QualifiedName = qualifiedName, FilePath = filePath,
            Properties = properties ?? []
        };
        buffer.AddNode(Node(NodeLabel.Struct, "Bridge", $"{Repository}:native/bridge.h#type:Bridge", "native/bridge.h"));
        buffer.AddNode(Node(NodeLabel.Function, "open_bridge", $"{Repository}:native/bridge.c#function:open_bridge()",
            "native/bridge.c", new() { ["receiver_owner"] = "Bridge" }));
        const string receiverCaller = $"{Repository}:App/A.swift#type:A#method:viaReceiver()";
        const string bareCaller = $"{Repository}:App/A.swift#type:A#method:bare()";
        buffer.AddNode(Node(NodeLabel.Method, "viaReceiver", receiverCaller, "App/A.swift"));
        buffer.AddNode(Node(NodeLabel.Method, "bare", bareCaller, "App/A.swift"));
        buffer.AddUnresolvedCall(new UnresolvedCall(receiverCaller, "open_bridge", "Bridge", 0.65, CallReceiverKind.Resolved));
        buffer.AddUnresolvedCall(new UnresolvedCall(bareCaller, "open_bridge", null, 0.7, CallReceiverKind.Bare));

        await new IndexingPipeline(
                new InMemoryGraphStore(), [], Options.Create(new IndexingOptions()), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .ResolveCallsAsync(Repository, buffer, includePersistedNodes: false, new HashSet<string>(), CancellationToken.None);

        buffer.AllPendingEdges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (edge.SourceQN, edge.TargetQN))
            .ShouldBe([(bareCaller, $"{Repository}:native/bridge.c#function:open_bridge()")]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsCallTarget_AReceiverCallNeverCrossesALanguageFamily(bool isBareCall, bool expected)
    {
        var cFunction = new GraphNode
        {
            Repository = Repository, Label = NodeLabel.Function, Name = "f", QualifiedName = "f", FilePath = "native/a.c"
        };
        CallLanguages.IsCallTarget("swift", cFunction, isBareCall).ShouldBe(expected);
        CallLanguages.IsCallTarget("rust", cFunction, isBareCall).ShouldBe(expected);
        // Within a family a receiver call binds as before.
        CallLanguages.IsCallTarget("c", cFunction, isBareCall).ShouldBeTrue();
    }

    /// <summary>
    /// The extern signature alone never makes a function linkable: only the recorded abi does,
    /// which both extractors set to a foreign ABI only for <c>#[no_mangle]</c> / <c>#[export_name]</c>.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("Rust", false)]
    [InlineData("C", true)]
    [InlineData("C-unwind", true)]
    public void ExportsCAbi_FollowsTheRecordedAbiNotTheSignature(string? abi, bool expected)
    {
        var properties = new Dictionary<string, object>
        {
            ["signature"] = "unsafe extern \"C\" fn count(ctx: *mut c_void, _: u64, _: u64)",
            ["scip_local_symbol"] = "local 12"
        };
        if (abi is not null)
            properties[CallLanguages.AbiProperty] = abi;
        var node = new GraphNode
        {
            Repository = Repository,
            Label = NodeLabel.Function,
            Name = "count",
            QualifiedName = $"{Repository}:scip-local:src/managed.rs:local 12",
            FilePath = "src/managed.rs",
            Properties = properties
        };

        CallLanguages.ExportsCAbi(node).ShouldBe(expected);
        CallLanguages.IsCallTarget("c", node).ShouldBe(expected);
        CallLanguages.CanCall("swift", node).ShouldBe(expected);
        CallLanguages.CanCall("python", node).ShouldBeFalse();
    }

    [Fact]
    public void FamilyOfCaller_FallsBackToTheFileOfTheCallerQualifiedName()
    {
        CallLanguages.FamilyOfQualifiedName(Repository, $"{Repository}:native/gennmtab.c#function:total()").ShouldBe("c");
        CallLanguages.FamilyOfQualifiedName(Repository, $"{Repository}:src/lib.rs").ShouldBe("rust");
        CallLanguages.FamilyOfQualifiedName(Repository, "Other:src/lib.rs#function:f()").ShouldBeNull();
        CallLanguages.FamilyOfQualifiedName(Repository, $"{Repository}:scip:rust-analyzer cargo x f().").ShouldBeNull();
    }

    [Theory]
    [InlineData("pub extern \"C\" fn lw_open() -> u32", "C")]
    [InlineData("pub unsafe extern \"C-unwind\" fn lw_open()", "C-unwind")]
    [InlineData("extern fn lw_open()", "C")]
    [InlineData("pub extern \"Rust\" fn lw_open()", "Rust")]
    [InlineData("pub fn lw_open() -> u32", null)]
    public void ParseExternAbi_ReadsTheAbiOfAScipSignature(string signature, string? expected)
    {
        CallLanguages.ParseExternAbi(signature).ShouldBe(expected);
    }

    [Fact]
    public void CanCall_NodesOfUnknownLanguageAreNotFiltered()
    {
        var stub = new GraphNode { Repository = Repository, Label = NodeLabel.Method, Name = "convert", QualifiedName = "convert" };
        CallLanguages.CanCall("swift", stub).ShouldBeTrue();
        CallLanguages.CanCall(null, new GraphNode
        {
            Repository = Repository, Label = NodeLabel.Function, Name = "convert", QualifiedName = "py", FilePath = "a.py"
        }).ShouldBeTrue();
        CallLanguages.CanCall("javascript", new GraphNode
        {
            Repository = Repository, Label = NodeLabel.Function, Name = "f", QualifiedName = "js", FilePath = "a.js"
        }).ShouldBeTrue();
    }
}
