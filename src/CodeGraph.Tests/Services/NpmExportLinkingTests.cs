using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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

public sealed class NpmExportLinkingTests
{
    [Fact]
    public async Task DuplicateDefinitionsWithinOnePackageAndSameNamesWithoutIdentity_AreNeverGuessed()
    {
        var store = new InMemoryGraphStore();
        store.AddRepository("App"); store.AddRepository("SharedUI");
        var caller = store.AddNode(new GraphNode { Repository = "App", Label = NodeLabel.Component, Name = "App", QualifiedName = "App" });
        var external = store.AddNode(new GraphNode { Repository = "App", Label = NodeLabel.ExternalSymbol, Name = "Button",
            QualifiedName = "npm-symbol:@org/ui#Button", Properties = new() { ["package_name"] = "@org/ui", ["import_path"] = "@org/ui", ["export_name"] = "Button" } });
        store.AddEdge(new GraphEdge { Repository = "App", SourceId = caller, TargetId = external, Type = EdgeType.RENDERS });
        var aliases = JsonSerializer.Serialize(new[] { new { package_name = "@org/ui", import_path = "@org/ui", export_name = "Button" } });
        foreach (var name in new[] { "FirstActual", "SecondActual" }) store.AddNode(new GraphNode { Repository = "SharedUI",
            Label = NodeLabel.Component, Name = name, QualifiedName = name, Properties = new() { ["npm_exports"] = aliases } });
        var linker = new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance);
        await linker.LinkForRepositoryAsync("SharedUI", CancellationToken.None);
        store.CrossEdges.ShouldBeEmpty();
        var unrelated = new InMemoryGraphStore();
        unrelated.AddRepository("App"); unrelated.AddRepository("SharedUI");
        var source = unrelated.AddNode(new GraphNode { Repository = "App", Label = NodeLabel.Component, Name = "App", QualifiedName = "App" });
        var proxy = unrelated.AddNode(new GraphNode { Repository = "App", Label = NodeLabel.ExternalSymbol, Name = "Button",
            QualifiedName = "npm-symbol:@org/ui#Button", Properties = new() { ["package_name"] = "@org/ui", ["import_path"] = "@org/ui", ["export_name"] = "Button" } });
        unrelated.AddNode(new GraphNode { Repository = "SharedUI", Label = NodeLabel.Component, Name = "Button", QualifiedName = "Button" });
        unrelated.AddEdge(new GraphEdge { Repository = "App", SourceId = source, TargetId = proxy, Type = EdgeType.RENDERS });
        await new CrossRepoLinker(unrelated, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
        unrelated.CrossEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexedPackageExports_LinkRenderToActualDeclaration_AndReconcileAmbiguityRemovalAndRetargeting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-npm-exports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            void Write(string repo, string path, string content)
            {
                var full = Path.Combine(root, repo, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            Write("SceneWorks", "package.json", """{"name":"sceneworks-app","dependencies":{"@sceneworks/ui":"*"}}""");
            Write("SceneWorks", "src/app.tsx", """
                import { Button as Primary } from '@sceneworks/ui';
                import SubpathButton from '@sceneworks/ui/button';
                export function SceneWorksApp() { return <Primary/>; }
                export function SubpathScreen() { return <SubpathButton/>; }
                """);
            void Library(string repo, string name)
            {
                Write(repo, "package.json", """{"name":"@sceneworks/ui","exports":{".":"./src/index.ts","./button":"./src/button.tsx"}}""");
                Write(repo, "src/index.ts", "export { default as Button } from './button';");
                Write(repo, "src/button.tsx", $"export default function {name}() {{ return <button/>; }}");
            }
            Library("SharedUI", "PrimaryActual");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            var linker = new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            async Task Index(string repo)
            {
                await pipeline.IndexRepositoryAsync(repo, Path.Combine(root, repo), ct: timeout.Token);
                await linker.LinkForRepositoryAsync(repo, timeout.Token);
            }
            List<CrossRepoEdge> Links() => store.CrossEdges.Where(edge => edge.Properties.GetValueOrDefault("semantic_owner")?.ToString() == "npm_exports").ToList();
            GraphNode Named(string repo, string name) => store.Nodes.Single(node => node.Repository == repo && node.Name == name);
            string[] Facts(InMemoryGraphStore? graph = null)
            {
                graph ??= store;
                return graph.CrossEdges.Where(edge => edge.Properties.GetValueOrDefault("semantic_owner")?.ToString() == "npm_exports")
                    .Select(edge => $"{graph.Nodes.Single(node => node.Id == edge.SourceNodeId).QualifiedName}|{graph.Nodes.Single(node => node.Id == edge.TargetNodeId).QualifiedName}|{edge.Type}|{edge.Properties["import_path"]}|{edge.Properties["export_name"]}")
                    .Order(StringComparer.Ordinal).ToArray();
            }
            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var mcp = new CodeGraphMcpServer(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!, impact);
            async Task<string> Impact(GraphNode changed) => string.Join('\n', (await mcp.AnalyzeImpact(changed.QualifiedName, changed.Repository, 5))
                .Content.OfType<TextContentBlock>().Select(block => block.Text));

            await Index("SceneWorks");
            Links().ShouldBeEmpty();
            await Index("SharedUI");
            var actual = Named("SharedUI", "PrimaryActual");
            actual.Properties.ShouldContainKey("npm_exports");
            Links().Count.ShouldBe(2);
            Links().ShouldAllBe(edge => edge.Type == EdgeType.RENDERS && edge.TargetNodeId == actual.Id && edge.TargetRepository == "SharedUI");
            var source = Named("SceneWorks", "SceneWorksApp");
            var subpath = Named("SceneWorks", "SubpathScreen");
            store.Edges.ShouldContain(edge => edge.SourceId == source.Id && edge.Type == EdgeType.RENDERS
                && store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName == "npm-symbol:@sceneworks/ui#Button");
            (await Impact(actual)).ShouldContain("SceneWorksApp");
            (await Impact(actual)).ShouldContain("SubpathScreen");
            var stableIds = Links().Select(edge => edge.Id).Order().ToArray();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index("SharedUI");
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Links().Select(edge => edge.Id).Order().ShouldBe(stableIds);
            var support = store.AddNode(new GraphNode { Repository = "Support", Label = NodeLabel.Class,
                Name = "Dependency", QualifiedName = "Dependency" });
            await store.InsertCrossRepoEdgeAsync(new CrossRepoEdge { SourceRepository = "SceneWorks", TargetRepository = "Support",
                SourceNodeId = source.Id, TargetNodeId = support, Type = EdgeType.USES_TYPE, Properties = new() { ["source"] = "independent" } });
            var independentId = store.CrossEdges.Single(edge => edge.TargetNodeId == support).Id;

            Library("RivalUI", "RivalActual");
            await Index("RivalUI");
            Links().ShouldBeEmpty("a new provider must retract the existing SceneWorks-to-SharedUI link despite being a third repository");
            (await Impact(actual)).ShouldNotContain("SceneWorksApp");
            store.CrossEdges.ShouldContain(edge => edge.Id == independentId);
            // Removing the rival's public entries restores the original exact package/export match.
            Write("RivalUI", "package.json", """{"name":"@sceneworks/ui","exports":{"./private":"./src/button.tsx"}}""");
            await Index("RivalUI");
            Links().Count.ShouldBe(2);
            Links().ShouldAllBe(edge => edge.TargetRepository == "SharedUI");
            Named("SceneWorks", "SceneWorksApp").Id.ShouldBe(source.Id);
            store.CrossEdges.ShouldContain(edge => edge.Id == independentId);

            Write("SharedUI", "src/alternate.tsx", "export function AlternateActual() { return <b/>; }");
            Write("SharedUI", "src/index.ts", "export { AlternateActual as Button } from './alternate';");
            await Index("SharedUI");
            var alternate = Named("SharedUI", "AlternateActual");
            Links().Single(edge => edge.SourceNodeId == source.Id).TargetNodeId.ShouldBe(alternate.Id);
            Links().Single(edge => edge.SourceNodeId == subpath.Id).TargetNodeId.ShouldBe(Named("SharedUI", "PrimaryActual").Id);
            (await Impact(alternate)).ShouldContain("SceneWorksApp");
            (await Impact(alternate)).ShouldNotContain("SubpathScreen");
            var current = Facts();
            stableIds = Links().Select(edge => edge.Id).Order().ToArray();
            await Index("SharedUI");
            Facts().ShouldBe(current);
            Links().Select(edge => edge.Id).Order().ShouldBe(stableIds);
            var fresh = new InMemoryGraphStore();
            var freshPipeline = new IndexingPipeline(fresh, [], Options.Create(new IndexingOptions()), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            foreach (var repo in new[] { "SceneWorks", "SharedUI", "RivalUI" })
                await freshPipeline.IndexRepositoryAsync(repo, Path.Combine(root, repo), ct: timeout.Token);
            await new CrossRepoLinker(fresh, NullLogger<CrossRepoLinker>.Instance).LinkAsync(timeout.Token);
            Facts().ShouldBe(Facts(fresh));
        }
        finally { Directory.Delete(root, true); }
    }
}
