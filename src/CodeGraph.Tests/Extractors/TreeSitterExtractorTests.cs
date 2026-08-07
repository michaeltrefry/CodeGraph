using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.Options;

namespace CodeGraph.Tests.Extractors;

public class TreeSitterExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    public static TheoryData<string, string, string> CallFixtures => new()
    {
        { ".c", "void callee(void) {}\nvoid caller(void) { callee(); }", "C" },
        { ".cpp", "void callee() {}\nvoid caller() { callee(); }", "C++" },
        { ".py", "def callee():\n    pass\n\ndef caller():\n    callee()\n", "Python" },
        { ".go", "package demo\nfunc callee() {}\nfunc caller() { callee() }\n", "Go" },
        { ".java", "class Demo { void callee() {} void caller() { callee(); } }", "Java" },
        { ".rb", "def callee; end\ndef caller; callee(); end\n", "Ruby" },
        { ".rs", "fn callee() {}\nfn caller() { callee(); }\n", "Rust" },
        { ".php", "<?php function callee() {} function caller() { callee(); }", "PHP" },
        { ".sh", "callee() { :; }\ncaller() { callee; }\n", "Bash" }
    };

    public static TheoryData<string, string> QualifiedCallFixtures => new()
    {
        { ".cpp", "class Worker { public: void helper() {} void run() { this->helper(); Worker::helper(); } };" },
        { ".py", "class Worker:\n    def helper(self): pass\n    def run(self):\n        self.helper()\n        Worker.helper(self)\n" },
        { ".go", "package demo\ntype Worker struct{}\nfunc (w Worker) helper() {}\nfunc (w Worker) run() { w.helper(); Worker.helper(w) }\n" },
        { ".java", "class Worker { void helper() {} void run() { this.helper(); Worker.helper(); } }" },
        { ".rb", "class Worker\n  def helper; end\n  def run; self.helper; Worker.helper; end\nend\n" },
        { ".rs", "struct Worker; impl Worker { fn helper(&self) {} fn run(&self) { self.helper(); Worker::helper(self); } }" },
        { ".php", "<?php class Worker { function helper() {} function run() { $this->helper(); Worker::helper(); } }" }
    };

    public static TheoryData<string, string> LowercaseScopedCallFixtures => new()
    {
        { ".cpp", "namespace worker { void helper() {} void run() { worker::helper(); } }" },
        { ".rs", "struct worker; impl worker { fn helper(&self) {} fn run(&self) { worker::helper(self); } }" },
        { ".php", "<?php class worker { function helper() {} function run() { worker::helper(); } }" }
    };

    public static TheoryData<string, string> AmbiguousReceiverFixtures => new()
    {
        {
            ".java",
            "class worker { void helper() {} } class Runner { actual worker; void run() { worker.helper(); } } class actual {}"
        },
        {
            ".cpp",
            "class worker { public: void helper() {} }; class Runner { actual worker; void run() { worker.helper(); } }; class actual {};"
        },
        {
            ".py",
            "class worker:\n    def helper(self): pass\nclass Runner:\n    def run(self):\n        import module as worker\n        worker.helper()\n"
        },
        {
            ".go",
            "package demo\ntype worker struct{}\nfunc (worker) helper() {}\ntype Runner struct { worker actual }\nfunc (r Runner) run() { r.worker.helper() }\n"
        },
        {
            ".cpp",
            "namespace right { void helper() {} } namespace runner { void run() { missing::right::helper(); } }"
        }
    };

    public static TheoryData<string, string, string> SplitOwnerFixtures => new()
    {
        {
            ".go",
            "package demo\ntype Worker struct{}\n",
            "package demo\nfunc (w Worker) helper() {}\nfunc (w Worker) run() { w.helper() }\n"
        },
        {
            ".rs",
            "struct Worker;\n",
            "impl Worker { fn helper(&self) {} fn run(&self) { self.helper(); } }\n"
        }
    };

    [Fact]
    public async Task ExtractAsync_CStructs_GetLineRanges()
    {
        var source = """
            typedef struct {
                int rpm;
                int current_ma;
            } motor_state_t;
            """;

        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var result = await extractor.ExtractAsync("/test/motor_state.h", source, TestContext);

        var structNode = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Struct &&
            n.Name == "motor_state_t");

        structNode.StartLine.ShouldBe(1);
        structNode.EndLine.ShouldBeGreaterThan(structNode.StartLine);
    }

    [Fact]
    public async Task ExtractAsync_RustCaseDistinctSymbols_GetStableSourceScopedQualifiedNames()
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var typeResult = await extractor.ExtractAsync(
            "/test/crates/core/src/memory_calibration.rs",
            "pub struct Strategy { pub rung: u32 }",
            TestContext);
        var functionResult = await extractor.ExtractAsync(
            "/test/crates/worker/src/candle_memory_strategy.rs",
            "fn strategy(rung: u32) -> u32 { rung }",
            TestContext);

        var type = typeResult.Nodes.ShouldHaveSingleItem();
        var function = functionResult.Nodes.ShouldHaveSingleItem();

        type.QualifiedName.ShouldBe(
            "TestProject:crates/core/src/memory_calibration.rs#type:Strategy");
        function.QualifiedName.ShouldBe(
            "TestProject:crates/worker/src/candle_memory_strategy.rs#function:strategy(rung:u32)");
        string.Equals(type.QualifiedName, function.QualifiedName, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse();
        typeResult.Edges.ShouldContain(edge =>
            edge.SourceQN == "TestProject:crates/core/src/memory_calibration.rs" &&
            edge.TargetQN == type.QualifiedName);
        functionResult.Edges.ShouldContain(edge =>
            edge.SourceQN == "TestProject:crates/worker/src/candle_memory_strategy.rs" &&
            edge.TargetQN == function.QualifiedName);
    }

    [Theory]
    [MemberData(nameof(CallFixtures))]
    public async Task ExtractAsync_EachSupportedLanguage_AttributesCallsToEnclosingFunction(
        string extension,
        string source,
        string expectedLanguage)
    {
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            $"/test/calls{extension}", source, TestContext);

        result.Metadata.ShouldNotBeNull();
        result.Metadata.Language.ShouldBe(expectedLanguage);
        var caller = result.Nodes.ShouldContain(node => node.Name == "caller");
        result.UnresolvedCalls.ShouldContain(call =>
            call.CallerQN == caller.QualifiedName
            && call.CalleeName == "callee");
    }

    [Theory]
    [MemberData(nameof(CallFixtures))]
    public async Task IndexProjectAsync_EachSupportedLanguage_ResolvesPositiveCallEdge(
        string extension,
        string source,
        string expectedLanguage)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"calls{extension}"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("CallEdgeRepo", rootPath, ct: CancellationToken.None);

            var nodesById = store.Nodes.ToDictionary(node => node.Id);
            store.Edges.ShouldContain(edge =>
                edge.Type == EdgeType.CALLS
                && nodesById[edge.SourceId].Name == "caller"
                && nodesById[edge.TargetId].Name == "callee");
            (await store.GetRepositoryByName("CallEdgeRepo"))!.Language.ShouldBe(expectedLanguage);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_MemberNestedAndUnknownCalls_AreHandledDeliberately()
    {
        var source = """
            import os

            class Worker:
                def helper(self):
                    pass

                def run(self, service):
                    self.helper()
                    service.missing()
                    factory()()
            """;
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var result = await extractor.ExtractAsync("/test/worker.py", source, TestContext);
        var worker = result.Nodes.ShouldContain(node => node.Name == "Worker");
        var run = result.Nodes.ShouldContain(node => node.Name == "run");

        result.UnresolvedImports.ShouldContain(import => import.ImportedNamespace == "os");
        result.UnresolvedCalls.ShouldContain(call =>
            call.CallerQN == run.QualifiedName
            && call.CalleeName == "helper"
            && call.ReceiverType == worker.QualifiedName
            && call.ReceiverKind == CallReceiverKind.Resolved);
        result.UnresolvedCalls.ShouldContain(call =>
            call.CallerQN == run.QualifiedName
            && call.CalleeName == "missing"
            && call.ReceiverType is null
            && call.ReceiverKind == CallReceiverKind.Unresolved
            && call.Confidence == 0.45);
        result.UnresolvedCalls.Count(call => call.CalleeName == "factory").ShouldBe(1);
    }

    [Fact]
    public async Task ExtractAsync_ScopedCall_PreservesResolvableTypeScope()
    {
        var source = """
            namespace Tools {
                void helper() {}
                void run() { Tools::helper(); }
            }
            """;
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);

        var result = await extractor.ExtractAsync("/test/tools.cpp", source, TestContext);
        var tools = result.Nodes.ShouldContain(node => node.Name == "Tools");
        var run = result.Nodes.ShouldContain(node => node.Name == "run");

        result.UnresolvedCalls.ShouldContain(call =>
            call.CallerQN == run.QualifiedName
            && call.CalleeName == "helper"
            && call.ReceiverType == tools.QualifiedName);
    }

    [Theory]
    [InlineData(".py", "class Worker:\n    def helper(self): pass\n    def run(self): self.helper()\n")]
    [InlineData(".cpp", "namespace Worker { void helper() {} void run() { Worker::helper(); } }")]
    public async Task IndexProjectAsync_QualifiedCalls_ResolveWithinEnclosingType(
        string extension,
        string source)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-qualified-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"qualified{extension}"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("QualifiedRepo", rootPath, ct: CancellationToken.None);

            var nodesById = store.Nodes.ToDictionary(node => node.Id);
            store.Edges.ShouldContain(edge =>
                edge.Type == EdgeType.CALLS
                && nodesById[edge.SourceId].Name == "run"
                && nodesById[edge.TargetId].Name == "helper");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(QualifiedCallFixtures))]
    public async Task IndexProjectAsync_MemberAndScopedCalls_ResolveWithinOwningTypeAndDeduplicate(
        string extension,
        string source)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-member-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"qualified{extension}"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("QualifiedRepo", rootPath, ct: CancellationToken.None);

            var nodesById = store.Nodes.ToDictionary(node => node.Id);
            var calls = store.Edges.Where(edge => edge.Type == EdgeType.CALLS).ToList();
            calls.Count.ShouldBe(1);
            nodesById[calls[0].SourceId].Name.ShouldBe("run");
            nodesById[calls[0].TargetId].Name.ShouldBe("helper");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(LowercaseScopedCallFixtures))]
    public async Task IndexProjectAsync_LowercaseScopedCalls_ResolveWithoutCapitalizationHeuristics(
        string extension,
        string source)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-lowercase-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"qualified{extension}"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("LowercaseRepo", rootPath, ct: CancellationToken.None);

            var nodesById = store.Nodes.ToDictionary(node => node.Id);
            var call = store.Edges.Single(edge => edge.Type == EdgeType.CALLS);
            nodesById[call.SourceId].Name.ShouldBe("run");
            nodesById[call.TargetId].Name.ShouldBe("helper");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_CrossFileDotReceiver_DoesNotPromoteTypeBySpelling()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-cross-file-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "worker.py"),
                "class worker:\n    def helper(self): pass\n");
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "runner.py"),
                "class Runner:\n    def run(self): worker.helper(self)\n");
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("CrossFileRepo", rootPath, ct: CancellationToken.None);

            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.CALLS);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_UnknownInstanceReceiver_DoesNotResolveByGlobalName()
    {
        var source = "class service:\n    def helper(self): pass\nclass Runner:\n    def run(self, service): service.helper()\n";
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var extraction = await extractor.ExtractAsync("/test/unknown.py", source, TestContext);
        extraction.UnresolvedCalls.Single().ReceiverKind.ShouldBe(CallReceiverKind.Unresolved);

        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-unknown-receiver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "unknown.py"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [extractor],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("UnknownReceiverRepo", rootPath, ct: CancellationToken.None);

            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.CALLS);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(AmbiguousReceiverFixtures))]
    public async Task IndexProjectAsync_FieldMemberAndImportAliases_DoNotPromoteKnownTypeNames(
        string extension,
        string source)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"collision{extension}"), source);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("CollisionRepo", rootPath, ct: CancellationToken.None);

            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.CALLS);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(SplitOwnerFixtures))]
    public async Task IndexProjectAsync_SplitFileGoAndRustMethods_RetainOwnerAndResolveCalls(
        string extension,
        string typeSource,
        string methodSource)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-split-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"type{extension}"), typeSource);
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"methods{extension}"), methodSource);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("SplitOwnerRepo", rootPath, ct: CancellationToken.None);

            var run = store.Nodes.ShouldContain(node => node.Name == "run");
            var helper = store.Nodes.ShouldContain(node => node.Name == "helper");
            run.Label.ShouldBe(NodeLabel.Method);
            helper.Label.ShouldBe(NodeLabel.Method);
            run.Properties["receiver_owner"].ShouldBe("Worker");
            var call = store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS);
            call.SourceId.ShouldBe(run.Id);
            call.TargetId.ShouldBe(helper.Id);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexProjectAsync_GenericRustImpl_NormalizesNominalOwnerAndResolvesSelfCall(
        bool splitFiles)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-rust-generic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            const string typeSource = "struct Worker<T>(T);\n";
            const string implSource =
                "impl<T> Worker<T> { fn helper(&self) {} fn run(&self) { self.helper(); } }\n";
            if (splitFiles)
            {
                await File.WriteAllTextAsync(Path.Combine(rootPath, "type.rs"), typeSource);
                await File.WriteAllTextAsync(Path.Combine(rootPath, "methods.rs"), implSource);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(rootPath, "worker.rs"), typeSource + implSource);
            }

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("GenericRustRepo", rootPath, ct: CancellationToken.None);

            var run = store.Nodes.ShouldContain(node => node.Name == "run");
            var helper = store.Nodes.ShouldContain(node => node.Name == "helper");
            run.Properties["receiver_owner"].ShouldBe("Worker");
            var call = store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS);
            call.SourceId.ShouldBe(run.Id);
            call.TargetId.ShouldBe(helper.Id);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndexProjectAsync_ScopedRustImpl_UsesUniqueNominalOwner(
        bool splitFiles,
        bool genericOwner)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-rust-scoped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var typeSource = genericOwner ? "struct Worker<T>(T);\n" : "struct Worker;\n";
            var implSource = genericOwner
                ? "impl<T> foo::Worker<T> { fn helper(&self) {} fn run(&self) { self.helper(); } }\n"
                : "impl foo::Worker { fn helper(&self) {} fn run(&self) { self.helper(); } }\n";
            if (splitFiles)
            {
                await File.WriteAllTextAsync(Path.Combine(rootPath, "type.rs"), typeSource);
                await File.WriteAllTextAsync(Path.Combine(rootPath, "methods.rs"), implSource);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(rootPath, "worker.rs"), typeSource + implSource);
            }

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("ScopedRustRepo", rootPath, ct: CancellationToken.None);

            var run = store.Nodes.ShouldContain(node => node.Name == "run");
            var helper = store.Nodes.ShouldContain(node => node.Name == "helper");
            run.Properties["receiver_owner"].ShouldBe("Worker");
            var call = store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS);
            call.SourceId.ShouldBe(run.Id);
            call.TargetId.ShouldBe(helper.Id);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_DuplicateSplitRustOwners_DoNotCrossLinkSymbolicMethod()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-rust-owner-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "local.rs"),
                "struct Worker; impl Worker { fn run(&self) { self.helper(); } }\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "remote_type.rs"), "struct Worker;\n");
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "remote_methods.rs"),
                "impl Worker { fn helper(&self) {} }\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 3 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("DuplicateRustOwnerRepo", rootPath, ct: CancellationToken.None);

            store.Nodes.Count(node => node.Name == "Worker").ShouldBe(2);
            store.Nodes.Count(node => node.Name == "helper").ShouldBe(1);
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.CALLS);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalCaller_ResolvesPersistedUnchangedCallee()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-tree-incremental-call-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var callerPath = Path.Combine(rootPath, "caller.py");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "callee.py"), "def callee():\n    pass\n");
            await File.WriteAllTextAsync(callerPath, "def caller():\n    callee()\n");
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("IncrementalCallRepo", rootPath, ct: CancellationToken.None);
            await File.WriteAllTextAsync(callerPath, "def caller():\n    # changed\n    callee()\n");
            await pipeline.IndexProjectAsync(
                "IncrementalCallRepo",
                rootPath,
                changedFilesOnly: ["caller.py"],
                ct: CancellationToken.None);

            var nodesById = store.Nodes.ToDictionary(node => node.Id);
            var call = store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS);
            nodesById[call.SourceId].Name.ShouldBe("caller");
            nodesById[call.TargetId].Name.ShouldBe("callee");
            nodesById[call.TargetId].FilePath.ShouldBe("callee.py");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }
}
