using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class FrontendBackendOperationIntegrationTests
{
    [Fact]
    public async Task TwoLazyFactoriesRenderingOneExport_PersistOneStableRenderFact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-lazy-render-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), """{"name":"lazy-fixture"}""");
            File.WriteAllText(Path.Combine(root, "target.jsx"), "export function Shared(){return <main/>}");
            File.WriteAllText(Path.Combine(root, "lazy.jsx"), """
                import {lazy} from 'react';
                function helper(loader,name){return lazy(async()=>{const module=await loader();return {default:module[name]};});}
                export function buildA(loader){const create=()=>helper(loader,'Shared');return create();}
                export function buildB(loader){const create=()=>helper(loader,'Shared');return create();}
                """);
            File.WriteAllText(Path.Combine(root, "App.jsx"), """
                import {buildA,buildB} from './lazy.jsx';
                const load=()=>import('./target.jsx');
                const A=buildA(load),B=buildB(load);
                export function App(){return <><A/><B/></>}
                """);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            IndexingPipeline Pipeline(InMemoryGraphStore graph) => new(graph, [], Options.Create(new IndexingOptions()),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => Pipeline(store).IndexRepositoryAsync("UI", root, ct: timeout.Token);
            await Index();
            var app = store.Nodes.Single(node => node.Name == "App" && node.FilePath == "App.jsx");
            var shared = store.Nodes.Single(node => node.Name == "Shared" && node.FilePath == "target.jsx");
            store.Edges.Count(edge => edge.SourceId == app.Id && edge.TargetId == shared.Id && edge.Type == EdgeType.RENDERS).ShouldBe(1);
            var writes = store.ReplaceRepositoryFilesCallCount;
            var baseline = ExtractionSnapshot(store);
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            ExtractionSnapshot(store).ShouldBe(baseline);
            var fresh = new InMemoryGraphStore();
            await Pipeline(fresh).IndexRepositoryAsync("UI", root, ct: timeout.Token);
            ExtractionSnapshot(store).ShouldBe(ExtractionSnapshot(fresh));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AngularProviderOnlyRetarget_ReplacesProvisionalCalls_AndMatchesFreshGraph()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-provider-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), """{"name":"provider-fixture"}""");
            File.WriteAllText(Path.Combine(root, "services.ts"), "export class Original {load(){}} export class First {load(){}} export class Second {load(){}}");
            const string providers = "import {Original, First, Second} from './services'; export const PROVIDERS=[{provide:Original,useClass:First}];";
            File.WriteAllText(Path.Combine(root, "providers.ts"), providers);
            File.WriteAllText(Path.Combine(root, "page.ts"), """
                import {Component} from '@angular/core';
                import {Original} from './services';
                import {PROVIDERS} from './providers';
                @Component({selector:'app-page',template:'',providers:PROVIDERS})
                export class Page {constructor(private api:Original){} run(){this.api.load();}}
                """);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            IndexingPipeline Pipeline(InMemoryGraphStore graph) => new(graph, [], Options.Create(new IndexingOptions()),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => Pipeline(store).IndexRepositoryAsync("UI", root, ct: timeout.Token);
            void AssertImplementation(string expected)
            {
                var run = store.Nodes.Single(node => node.Name == "run" && node.FilePath == "page.ts");
                var targets = store.Edges.Where(edge => edge.SourceId == run.Id && edge.Type == EdgeType.CALLS)
                    .Select(edge => store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName).ToList();
                targets.ShouldContain(qn => qn.EndsWith($".{expected}.load", StringComparison.Ordinal));
                targets.ShouldNotContain(qn => qn.EndsWith(".Original.load", StringComparison.Ordinal));
                targets.ShouldNotContain(qn => qn.EndsWith($".{(expected == "First" ? "Second" : "First")}.load", StringComparison.Ordinal));
            }
            await Index();
            AssertImplementation("First");
            var pageHash = (await store.GetFileHashRecordsAsync("UI"))["page.ts"].ContentHash;
            File.WriteAllText(Path.Combine(root, "providers.ts"), providers.Replace("useClass:First", "useClass:Second"));
            await Index();
            AssertImplementation("Second");
            (await store.GetFileHashRecordsAsync("UI"))["page.ts"].ContentHash.ShouldBe(pageHash);
            var fresh = new InMemoryGraphStore();
            await Pipeline(fresh).IndexRepositoryAsync("UI", root, ct: timeout.Token);
            ExtractionSnapshot(store).ShouldBe(ExtractionSnapshot(fresh));
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NextNestedPageAdditionAndDeletion_RefreshUnchangedLayoutEdges_AndMatchFreshGraph()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-next-layout-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "app", "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), """{"name":"next-fixture","dependencies":{"next":"*"}}""");
            File.WriteAllText(Path.Combine(root, "app", "layout.tsx"), "export default function Root({children}){return <html>{children}</html>}");
            File.WriteAllText(Path.Combine(root, "app", "page.tsx"), "export default function Home(){return <main/>}");
            File.WriteAllText(Path.Combine(root, "app", "nested", "layout.tsx"), "export default function Nested({children}){return <section>{children}</section>}");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            IndexingPipeline Pipeline(InMemoryGraphStore graph) => new(graph, [], Options.Create(new IndexingOptions()),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => Pipeline(store).IndexRepositoryAsync("UI", root, ct: timeout.Token);
            async Task AssertFresh()
            {
                var fresh = new InMemoryGraphStore();
                await Pipeline(fresh).IndexRepositoryAsync("UI", root, ct: timeout.Token);
                ExtractionSnapshot(store).ShouldBe(ExtractionSnapshot(fresh));
            }
            await Index();
            File.WriteAllText(Path.Combine(root, "app", "nested", "page.tsx"), "export default function Detail(){return <article/>}");
            await Index();
            var layout = store.Nodes.Single(node => node.Name == "Root" && node.FilePath == "app/layout.tsx");
            var nested = store.Nodes.Single(node => node.Name == "Nested" && node.FilePath == "app/nested/layout.tsx");
            store.Edges.ShouldContain(edge => edge.SourceId == layout.Id && edge.TargetId == nested.Id && edge.Type == EdgeType.RENDERS);
            await AssertFresh();
            File.Delete(Path.Combine(root, "app", "nested", "page.tsx"));
            await Index();
            await AssertFresh();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string[] ExtractionSnapshot(InMemoryGraphStore store)
    {
        static string Canonical(JsonNode? value) => value switch
        {
            JsonObject obj => "{" + string.Join(',', obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
            JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]",
            _ => value?.ToJsonString() ?? "null"
        };
        return store.Nodes.Where(node => node.Properties.ContainsKey("extraction_owner"))
            .Select(node => Canonical(JsonSerializer.SerializeToNode(new {kind="node", node.Label, node.Name,
                node.QualifiedName, node.FilePath, node.StartLine, node.EndLine, node.Properties})))
            .Concat(store.Edges.Where(edge => edge.Properties.ContainsKey("extraction_owner"))
                .Select(edge => Canonical(JsonSerializer.SerializeToNode(new {kind="edge",
                    source=store.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName,
                    target=store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName, edge.Type, edge.Properties}))))
            .Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData("axum")]
    [InlineData("gin")]
    public async Task RealFrontendAndNativePasses_PersistHttpHandlerImpact_AndReconcileBackendOnlyChanges(string framework)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        var axum = framework == "axum";
        var endpoint = axum ? "/api/v1/workflows/inspect" : "/admin/v1/metrics/summary";
        var handlerName = axum ? "inspect_workflow" : "MetricsSummaryHandler";
        var registration = axum ? "apps/rust-api/src/lib.rs" : "internal/server/server.go";
        var native = axum ? """
            use axum::{Router,routing::post};
            async fn inspect_workflow() {}
            fn router() -> Router { Router::new().route("/api/v1/workflows/inspect", post(inspect_workflow)) }
            """ : """
            package server
            import (
              "github.com/gin-gonic/gin"
              "example.com/router/internal/api/admin"
            )
            func Setup(engine *gin.Engine) { group := engine.Group("/admin/v1"); group.GET("/metrics/summary", admin.MetricsSummaryHandler()) }
            """;
        try
        {
            Write("package.json", """{"name":"operation-fixture"}""");
            Write(registration, native);
            if (axum)
            {
                Write("apps/web/src/api.js", """
                    const BASE = import.meta.env.VITE_API_BASE_URL ?? '';
                    export async function apiFetch(path, token, options = {}) { return fetch(`${BASE}${path}`, {...options}); }
                    export function inspect() { return apiFetch('/api/v1/workflows/inspect', null, {method:'POST'}); }
                    """);
                Write("apps/web/src/App.jsx", "import {inspect} from './api.js'; export function App() { return <button onClick={inspect}/>; }");
            }
            else
            {
                Write("go.mod", "module example.com/router\n");
                Write("internal/api/admin/metrics.go", "package admin\nfunc MetricsSummaryHandler() {}\n");
                Write("frontend/next.config.ts", "export default {basePath:'/ui'};");
                Write("frontend/src/lib/api.ts", "async function request(path, init=undefined) { return fetch('/admin/v1'+path, init); } export function getSummary() { return request('/metrics/summary'); }");
                Write("frontend/src/app/page.tsx", "import {useEffect} from 'react'; import {getSummary} from '../lib/api'; export default function Dashboard() {useEffect(()=>{getSummary();},[]); return <main/>;}");
            }
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var store = new InMemoryGraphStore();
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var tree = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
            var pipeline = new IndexingPipeline(store, [tree], Options.Create(new IndexingOptions {MaxParallelFiles=1}),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => pipeline.IndexRepositoryAsync("UI", root, ct: timeout.Token);
            await Index();
            var route = store.Nodes.Single(node => node.Label == NodeLabel.Route && node.Properties.GetValueOrDefault("route_template")?.ToString() == endpoint);
            var handler = store.Nodes.Single(node => node.Name == handlerName && node.Label is NodeLabel.Function or NodeLabel.Method);
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.HANDLES && edge.SourceId == route.Id && edge.TargetId == handler.Id);
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.HTTP_CALLS && edge.TargetId == route.Id);
            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var mcp = new CodeGraphMcpServer(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!, impact);
            var answer = await mcp.AnalyzeImpact(handler.QualifiedName, "UI", 5);
            string.Join("\n", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)).ShouldContain(axum ? "App" : "Dashboard");
            if (!axum) store.Nodes.ShouldContain(node => node.Properties.GetValueOrDefault("full_path")?.ToString() == "/ui");
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Write(registration, native.Replace(axum ? endpoint : "/metrics/summary", "/moved"));
            await Index();
            store.Nodes.ShouldNotContain(node => node.QualifiedName == route.QualifiedName);
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.HTTP_CALLS && edge.TargetId == route.Id);
            var before = JsonSerializer.Serialize(store.Nodes.OrderBy(node => node.QualifiedName));
            var hashes = await store.GetFileHashRecordsAsync("UI");
            Write(registration, axum ? "fn router(){ Router::new().route(" : "package server\nfunc Setup(){engine.GET(");
            await Should.ThrowAsync<InvalidOperationException>(Index);
            JsonSerializer.Serialize(store.Nodes.OrderBy(node => node.QualifiedName)).ShouldBe(before);
            (await store.GetFileHashRecordsAsync("UI"))[registration].ContentHash.ShouldBe(hashes[registration].ContentHash);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TauriWrapper_ReachesRegisteredRustCommand_AndRegistrationRemovalKeepsRealFunction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-tauri-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        try
        {
            Write("package.json", """{"name":"tauri-fixture"}""");
            Write("src-tauri/tauri.conf.json", """{"identifier":"test.ui","build":{"frontendDist":"../dist"}}""");
            Write("src-tauri/Cargo.toml", "[package]\nname=\"tauri-fixture\"\nversion=\"0.1.0\"\n");
            const string native = """
                #[tauri::command]
                fn get_app_overview() {}
                fn main() { tauri::Builder::default().invoke_handler(tauri::generate_handler![get_app_overview]); }
                """;
            Write("src-tauri/src/main.rs", native);
            Write("src/tauri.js", """
                import { invoke } from '@tauri-apps/api/core';
                async function readOverview(command, fallback) { if (!window.__TAURI_INTERNALS__) return fallback; return invoke(command); }
                export function loadOverview() { return readOverview('get_app_overview', {}); }
                """);
            Write("src/App.jsx", "import {loadOverview} from './tauri.js'; export function App() { return <button onClick={loadOverview}/>; }");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var store = new InMemoryGraphStore();
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var pipeline = new IndexingPipeline(store, [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions {MaxParallelFiles=1}), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => pipeline.IndexRepositoryAsync("UI", root, ct: timeout.Token);
            await Index();
            var function = store.Nodes.Single(node => node.Name == "get_app_overview" && node.Label == NodeLabel.Function
                && node.FilePath == "src-tauri/src/main.rs" && node.Properties.GetValueOrDefault("extraction_owner")?.ToString() != "tauri");
            var mcp = new CodeGraphMcpServer(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!,
                new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance));
            var answer = await mcp.AnalyzeImpact(function.QualifiedName, "UI", 5);
            string.Join("\n", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)).ShouldContain("App");
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Write("src-tauri/src/main.rs", native.Replace("generate_handler![get_app_overview]", "generate_handler![]"));
            await Index();
            store.Nodes.ShouldContain(node => node.QualifiedName == function.QualifiedName);
            var currentFunctionId = store.Nodes.Single(node => node.QualifiedName == function.QualifiedName).Id;
            store.Edges.ShouldNotContain(edge => edge.TargetId == currentFunctionId && edge.Properties.ContainsKey("extraction_owner") && edge.Properties["extraction_owner"].ToString() == "tauri");
            var unchanged = await store.GetFileHashRecordsAsync("UI");
            unchanged.Keys.ShouldContain("src/tauri.js");
            unchanged.Keys.ShouldContain("src-tauri/src/main.rs");
            writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AngularPageTemplateAndService_PersistHandlerImpact_AndRefreshHtmlOnlyChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-angular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        try
        {
            Write("package.json", """{"name":"angular-fixture"}""");
            Write("src/api.ts", """
                import { Injectable } from '@angular/core'; import { HttpClient } from '@angular/common/http';
                @Injectable() export class ApiService { constructor(private http: HttpClient) {} load() { return this.http.get('/api/items'); } }
                """);
            Write("src/page.ts", """
                import {Component} from '@angular/core'; import {ApiService} from './api'; import {Child} from './child';
                @Component({selector:'app-page', templateUrl:'./page.html', imports:[Child]})
                export class Page { constructor(private api:ApiService) {} ngOnInit() { this.api.load(); } }
                """);
            Write("src/child.ts", "import {Component} from '@angular/core'; @Component({selector:'child-panel',template:''}) export class Child {}");
            Write("src/page.html", "<child-panel/>");
            Write("src/routes.ts", "import {Routes,provideRouter} from '@angular/router'; const routes:Routes=[{path:'items',loadComponent:()=>import('./page').then(m=>m.Page)}]; provideRouter(routes);");
            Write("backend.rs", "use axum::{Router,routing::get}; async fn items() {} fn router()->Router { Router::new().route(\"/api/items\",get(items)) }");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var store = new InMemoryGraphStore();
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var pipeline = new IndexingPipeline(store, [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions {MaxParallelFiles=1}), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => pipeline.IndexRepositoryAsync("UI", root, ct: timeout.Token);
            await Index();
            var page = store.Nodes.Single(node => node.Name == "Page" && node.Label == NodeLabel.Component);
            var child = store.Nodes.Single(node => node.Name == "Child" && node.Label == NodeLabel.Component);
            store.Nodes.ShouldContain(node => node.Properties.GetValueOrDefault("full_path")?.ToString() == "/items");
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.RENDERS && edge.SourceId == page.Id && edge.TargetId == child.Id);
            var handler = store.Nodes.Single(node => node.Name == "items" && node.Label == NodeLabel.Function);
            var mcp = new CodeGraphMcpServer(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!,
                new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance));
            var answer = await mcp.AnalyzeImpact(handler.QualifiedName, "UI", 5);
            string.Join("\n", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)).ShouldContain("Page");
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Write("src/page.html", "<!-- <child-panel/> -->");
            await Index();
            var nextPage = store.Nodes.Single(node => node.QualifiedName == page.QualifiedName);
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.RENDERS && edge.SourceId == nextPage.Id);
            (await store.GetFileHashRecordsAsync("UI")).Keys.ShouldContain("src/page.ts");
            writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }
}
