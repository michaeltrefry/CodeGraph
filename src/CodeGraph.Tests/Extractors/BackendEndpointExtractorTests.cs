using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public sealed class BackendEndpointExtractorTests
{
    private readonly TreeSitterExtractor _extractor = new(NullLogger<TreeSitterExtractor>.Instance);
    private readonly ExtractorContext _context = new() { RepositoryName = "app", RootPath = "/test" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Axum_UsesExistingFallbackOrSemanticFunctions(bool semantic)
    {
        const string source = """
            use axum::{Router, routing::{get, post}};
            async fn health() {}
            async fn inspect_workflow() {}
            fn router() {
                let router = Router::new()
                    .route("/api/v1/health", get(health))
                    .route("/api/v1/workflows/inspect", post(inspect_workflow).get(health))
                    .with_state(state);
            }
            """;
        var definitions = await _extractor.ExtractAsync("/test/src/lib.rs", source, _context);
        var nodes = definitions.Nodes.Select(n => semantic && n.Label == NodeLabel.Function
            ? n with { QualifiedName = "app:scip:" + n.Name, Properties = new() { ["source"] = "scip" } } : n).ToList();
        var result = await _extractor.ExtractBackendEndpointsAsync("/test/src/lib.rs", source, _context, nodes);
        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.Nodes.Count.ShouldBe(3);
        result.Nodes.ShouldAllBe(n => n.Label == NodeLabel.Route && n.Properties["framework"].Equals("axum"));
        result.Edges.Count.ShouldBe(3);
        var inspect = result.Edges.Single(e => e.SourceQN == "route:app:POST:/api/v1/workflows/inspect");
        inspect.TargetQN.ShouldBe(nodes.Single(n => n.Name == "inspect_workflow").QualifiedName);
        result.Nodes.Single(n => n.QualifiedName == inspect.SourceQN).FilePath.ShouldBe("src/lib.rs");
    }

    [Fact]
    public async Task Axum_NestedMergedAndAliasedRoutersComposePathsWithoutGhostRoutes()
    {
        const string source = """
            use axum::{Router as Api, routing::get as read};
            fn health() {}
            fn build() {
                // Api::new().route("/comment", read(health));
                let ghost = "Api::new().route(\"/string\", read(health))";
                let child = Api::new().route("/health", read(health));
                let child_alias = child;
                let root = Api::new().nest("/api", child_alias).merge(Api::new().route("/ready", read(health)));
            }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.Nodes.Select(n => n.QualifiedName).Order().ShouldBe(new[] { "route:app:GET:/api/health", "route:app:GET:/ready" });
        result.Edges.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Axum_SameNamedOtherModuleDoesNotBindAndDynamicPathIsVisible()
    {
        const string source = """
            use axum::{Router, routing::get};
            mod other { fn handle() {} }
            fn build() {
                let r = Router::new().route("/missing", get(handle)).route(PATH, get(other::handle));
            }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Count.ShouldBe(2);
        result.Edges.ShouldBeEmpty();
        result.Nodes.ShouldContain(n => n.Properties["resolution_status"].Equals("dynamic_path"));
    }

    [Fact]
    public async Task Axum_ImportedHandlerResolvesOnlyToExplicitModuleFile()
    {
        const string source = """
            use axum::{Router, routing::post};
            use crate::handlers::inspect_workflow as inspect;
            fn build() { let r = Router::new().route("/api/v1/workflows/inspect", post(inspect)); }
            """;
        var candidates = new[] { Handler("inspect_workflow", "src/handlers.rs"), Handler("inspect_workflow", "other/src/handlers.rs") };
        var result = await _extractor.ExtractBackendEndpointsAsync("/test/src/lib.rs", source, _context, candidates);
        result.Edges.Single().TargetQN.ShouldBe(candidates[0].QualifiedName);
    }

    [Fact]
    public async Task Gin_GroupAliasesNestedPrefixesAndFactoryHandlersBindImportedPackage()
    {
        const string source = """
            package server
            import (
                "github.com/gin-gonic/gin"
                "example.com/router/internal/admin"
            )
            func register(engine *gin.Engine) {
                grp := engine.Group("/admin/v1", auth)
                alias := grp
                alias.GET("/metrics/summary", middleware(), admin.MetricsSummaryHandler(proxySvc))
                nested := alias.Group("/users")
                nested.POST("/:id", admin.Update)
                engine.DELETE("/sessions/:id", admin.Delete)
            }
            """;
        var candidates = new[] { Handler("MetricsSummaryHandler", "internal/admin/metrics.go"), Handler("Update", "internal/admin/update.go"),
            Handler("Delete", "internal/admin/delete.go"), Handler("MetricsSummaryHandler", "unrelated/admin/metrics.go") };
        var result = await _extractor.ExtractBackendEndpointsAsync("/test/internal/server/server.go", source, new ExtractorContext { RepositoryName = _context.RepositoryName, RootPath = _context.RootPath,
            GoModuleManifests = new Dictionary<string, string> { ["go.mod"] = "module example.com/router\n" } }, candidates);
        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.Nodes.Count.ShouldBe(3);
        result.Nodes.ShouldContain(n => n.QualifiedName == "route:app:GET:/admin/v1/metrics/summary");
        result.Nodes.ShouldContain(n => n.QualifiedName == "route:app:POST:/admin/v1/users/:id");
        result.Edges.Count.ShouldBe(3);
        result.Edges.Single(e => e.SourceQN.Contains("metrics/summary")).TargetQN.ShouldBe(candidates[0].QualifiedName);
    }

    [Fact]
    public async Task Gin_DirectVerbsRawStringsHandleAndCommentGhosts()
    {
        const string source = """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func main() {
                r := gin.Default()
                r.GET(`/health`, handle)
                r.PUT("/item", handle)
                r.PATCH("/item", handle)
                r.DELETE("/item", handle)
                r.HEAD("/item", handle)
                r.OPTIONS("/item", handle)
                r.Handle("POST", "/item", handle)
                // r.GET("/ghost", handle)
                s := "r.GET(\"/string\", handle)"
            }
            """;
        var result = await Extract("main.go", source);
        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.Nodes.Count.ShouldBe(7);
        result.Edges.Count.ShouldBe(7);
    }

    [Fact]
    public async Task RegistrationEditsRemoveAndRetargetAndFailureIsNotAnEmptySuccess()
    {
        const string source = """
            use axum::{Router, routing::get};
            fn one() {}
            fn two() {}
            fn build() { let r = Router::new().route("/health", get(one)); }
            """;
        var before = await Extract("src/lib.rs", source);
        var after = await Extract("src/lib.rs", source.Replace("get(one)", "get(two)"));
        before.Nodes.Single().QualifiedName.ShouldBe(after.Nodes.Single().QualifiedName);
        before.Edges.Single().TargetQN.ShouldNotBe(after.Edges.Single().TargetQN);
        var removed = await Extract("src/lib.rs", source.Replace(".route(\"/health\", get(one))", ""));
        removed.Succeeded.ShouldBeTrue();
        removed.Nodes.ShouldBeEmpty();
        var failed = await _extractor.ExtractBackendEndpointsAsync("/test/src/lib.rs", source + "fn broken(", _context, []);
        failed.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task UnrelatedFrameworksAndAmbiguousHandlersRemainUnbound()
    {
        const string source = """
            use axum::{Router, routing::get};
            fn handle() {}
            fn build() { let r = Router::new().route("/health", get(handle)); }
            """;
        var definitions = await _extractor.ExtractAsync("/test/src/lib.rs", source, _context);
        var handle = definitions.Nodes.Single(n => n.Name == "handle");
        var result = await _extractor.ExtractBackendEndpointsAsync("/test/src/lib.rs", source, _context,
            [handle, handle with { QualifiedName = "duplicate" }]);
        result.Edges.ShouldBeEmpty();
        result.Nodes.Single().Properties["resolution_status"].ShouldBe("ambiguous_handler");
        var unrelated = await Extract("main.go", "package main\nfunc main() { r := other.New(); r.GET(\"/ghost\", handler) }");
        unrelated.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Axum_InlineModulesAndScopedImportsDoNotCrossBind()
    {
        const string source = """
            use axum::{Router, routing::get};
            mod first {
                use axum::{Router, routing::get};
                fn handle() {}
                fn build() { let r = Router::new().route("/first", get(handle)); }
            }
            mod second {
                use axum::{Router, routing::get};
                use crate::elsewhere::handle;
                fn build() { let r = Router::new().route("/second", get(handle)); }
            }
            fn build() { let r = Router::new().route("/explicit", get(first::handle)); }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Count.ShouldBe(3);
        result.Edges.Count.ShouldBe(2);
        result.Edges.ShouldNotContain(e => e.SourceQN.Contains("second"));
        result.Edges.Select(e => e.TargetQN).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task ShadowedHandlersRemainUnresolvedAndTrailingSlashIsPreserved()
    {
        const string source = """
            use axum::{Router, routing::get};
            fn handle() {}
            fn build() { let handle = make_handler(); let r = Router::new().route("/health/", get(handle)); }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Single().QualifiedName.ShouldBe("route:app:GET:/health/");
        result.Edges.ShouldBeEmpty();
        const string go = """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func main() { r := gin.New(); g := r.Group("/api"); g.GET("/health/", handle); r.Handle(method, "/dynamic", handle) }
            """;
        var gin = await Extract("main.go", go);
        gin.Nodes.ShouldContain(n => n.QualifiedName == "route:app:GET:/api/health/");
        gin.Nodes.ShouldContain(n => n.Properties["resolution_status"].Equals("dynamic_method"));
        gin.Edges.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ConflictingRegistrationsDoNotChooseOneHandler()
    {
        const string source = """
            use axum::{Router, routing::get};
            fn one() {}
            fn two() {}
            fn build() { let r = Router::new().route("/same", get(one)).route("/same", get(two)); }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Single().Properties["resolution_status"].ShouldBe("ambiguous_handler");
        result.Edges.ShouldBeEmpty();
    }

    [Fact]
    public async Task GrammarFailureDoesNotReplacePriorEndpointsWithEmptyOutput()
    {
        var unavailable = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance,
            _ => new LanguageSpec { LanguageName = "Rust", Framework = null, GetLanguage = () => throw new InvalidOperationException("Unavailable grammar") });
        var result = await unavailable.ExtractBackendEndpointsAsync("/test/src/lib.rs", "", _context, []);
        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("Unavailable grammar");
    }

    [Fact]
    public void AdmissionIsConservativeAndIncludesMultilineRegistrationSyntax()
    {
        _extractor.MayContainBackendEndpoints(".rs", "fn broken(").ShouldBeFalse();
        _extractor.MayContainBackendEndpoints(".rs", "router.\n route(\"/x\", get(handler))").ShouldBeTrue();
        _extractor.MayContainBackendEndpoints(".go", "r.GET(\"/x\", handler)").ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnyRegistrationsExpandIntoEveryConcreteSupportedMethod(bool rust)
    {
        var source = rust ? """
            use axum::{Router, routing::any};
            fn handle() {}
            fn build() { let r = Router::new().route("/wild", any(handle)); }
            """ : """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func main() { r := gin.New(); r.Any("/wild", handle) }
            """;
        var result = await Extract(rust ? "src/lib.rs" : "main.go", source);
        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.Nodes.Select(node => node.Properties["http_method"].ToString()).Order().ShouldBe(
            CodeGraph.Services.Pipeline.CrossRepoLinker.HttpMethods.Order());
        result.Nodes.ShouldAllBe(node => node.QualifiedName == $"route:app:{node.Properties["http_method"]}:/wild");
        result.Edges.Count.ShouldBe(9);
        result.Edges.Select(edge => edge.TargetQN).Distinct().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("any(handle).post(specific)")]
    [InlineData("any(handle)).route(\"/wild\", post(specific)")]
    public async Task AxumExplicitMethodOverridesWildcardWithinOneRouterTree(string registration)
    {
        var source = $$$"""
            use axum::{Router, routing::{any, post}};
            fn handle() {}
            fn specific() {}
            fn build() { let r = Router::new().route("/wild", {{{registration}}}); }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Count.ShouldBe(9);
        result.Edges.Count.ShouldBe(9);
        result.Nodes.Single(node => node.Properties["http_method"].Equals("POST"))
            .Properties["handler_expression"].ShouldBe("specific");
        result.Nodes.Where(node => !node.Properties["http_method"].Equals("POST"))
            .ShouldAllBe(node => node.Properties["handler_expression"].Equals("handle"));
    }

    [Theory]
    [InlineData("get(specific)", "specific")]
    [InlineData("get(specific).head(handle)", "handle")]
    public async Task AxumWildcardHeadDispatchPrefersExplicitHeadThenGet(string methods, string headHandler)
    {
        var source = $$$"""
            use axum::{Router, routing::any};
            fn handle() {}
            fn specific() {}
            fn build() { let r = Router::new().route("/wild", any(handle).{{{methods}}}); }
            """;
        var result = await Extract("src/lib.rs", source);
        result.Nodes.Count.ShouldBe(9);
        result.Edges.Count.ShouldBe(9);
        result.Nodes.Single(node => node.Properties["http_method"].Equals("HEAD"))
            .Properties["handler_expression"].ShouldBe(headHandler);
        result.Nodes.Single(node => node.Properties["http_method"].Equals("GET"))
            .Properties["handler_expression"].ShouldBe("specific");
    }

    [Fact]
    public async Task GinConflictingWildcardAndExplicitRegistrationRemainsAmbiguous()
    {
        const string source = """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func specific() {}
            func main() { r := gin.New(); r.Any("/wild", handle); r.POST("/wild", specific) }
            """;
        var result = await Extract("main.go", source);
        result.Nodes.Count.ShouldBe(9);
        result.Nodes.Single(node => node.Properties["http_method"].Equals("POST"))
            .Properties["resolution_status"].ShouldBe("ambiguous_handler");
        result.Edges.Count.ShouldBe(8);
        result.Nodes.Single(node => node.Properties["http_method"].Equals("POST"))
            .Properties["registration_ambiguous"].ShouldBe(true);
    }

    [Fact]
    public async Task GinLiteralAnyMethodIsNotAFrameworkWildcardRegistration()
    {
        const string source = """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func main() { r := gin.New(); r.Handle("ANY", "/literal", handle) }
            """;
        var result = await Extract("main.go", source);
        result.Nodes.Single().Properties["http_method"].ShouldBe("ANY");
        result.Nodes.Single().Properties.ShouldNotContainKey("registration_method");
    }

    private async Task<ExtractionResult> Extract(string path, string source)
    {
        var definitions = await _extractor.ExtractAsync("/test/" + path, source, _context);
        return await _extractor.ExtractBackendEndpointsAsync("/test/" + path, source, _context, definitions.Nodes);
    }

    private static GraphNode Handler(string name, string file) => new()
    {
        Repository = "app", Name = name, Label = NodeLabel.Function, FilePath = file,
        QualifiedName = "app:" + file + "#" + name
    };
}
