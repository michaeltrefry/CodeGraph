using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>Exercises the shipped Node protocol, concrete analyzer, persistence and MCP together.
/// The Backend lane installs the sidecar dependencies before running the .NET suite.</summary>
public sealed class FrontendSidecarIntegrationTests
{
    [Fact]
    public async Task RealSidecar_RefreshesUnchangedCallersAndEntryRenders_AndPreservesFailedAnalysis()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-protocol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{"name":"ui-protocol","dependencies":{"react":"*"}}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var appPath = Path.Combine(root, "src/app.jsx");
            await File.WriteAllTextAsync(appPath, """
                import { useEffect } from 'react';
                import { load, View } from '../barrel.js';
                export function App() { useEffect(() => { load(); }, []); return <View/>; }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "entry.jsx"), "import { View } from './barrel.js'; <View/>;");
            await File.WriteAllTextAsync(Path.Combine(root, "helpers.js"), "export function first() {} export function second() {}");
            await File.WriteAllTextAsync(Path.Combine(root, "panel.jsx"), "export function One() { return <p/>; } export function Two() { return <b/>; }");
            var barrel = Path.Combine(root, "barrel.js");
            await File.WriteAllTextAsync(barrel, "export { first as load } from './helpers.js'; export { One as View } from './panel.jsx';");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            // An empty fallback list is intentional: the actual analyzer must supply discovery,
            // versioned hashes and structural File nodes for JavaScript-only projects itself.
            var pipeline = new IndexingPipeline(store, [], Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Task Index() => pipeline.IndexRepositoryAsync("UI", root, ct: timeout.Token);
            string Qn(string name) => store.Nodes.Single(node => node.Name == name).QualifiedName;
            long Id(string name) => store.Nodes.Single(node => node.Name == name).Id;
            string[] Facts() => store.Nodes.Where(node => node.Properties.ContainsKey("extraction_owner"))
                .Select(node => $"N:{node.QualifiedName}:{node.Label}:{node.StartLine}:{node.EndLine}")
                .Concat(store.Edges.Where(edge => edge.Properties.ContainsKey("extraction_owner"))
                    .Select(edge => $"E:{store.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName}:{store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName}:{edge.Type}:{JsonSerializer.Serialize(edge.Properties.OrderBy(pair => pair.Key))}"))
                .Order(StringComparer.Ordinal).ToArray();

            await Index();
            store.Nodes.ShouldContain(node => node.Name == "App" && node.Label == NodeLabel.Component);
            (await store.GetFileHashRecordsAsync("UI")).Keys.ShouldContain("src/app.jsx");
            var firstId = Id("first");
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS && edge.TargetId == firstId);
            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var fileExtractor = new TypeScriptExtractor(server, NullLogger<TypeScriptExtractor>.Instance);
            var singleFile = await fileExtractor.ExtractAsync(appPath, await File.ReadAllTextAsync(appPath),
                new ExtractorContext { RepositoryName = "UI", RootPath = root }, timeout.Token);
            singleFile.Succeeded.ShouldBeTrue();
            singleFile.Nodes.Single(node => node.Name == "App").QualifiedName.ShouldBe(Qn("App"));
            var mcp = new CodeGraphMcpServer(null!, null!, store, null!, Options.Create(new RepositorySourceOptions()), null!, impact,
                indexedLanguages: new IndexedLanguageCatalog([fileExtractor]));
            var answer = await mcp.AnalyzeImpact(Qn("first"), "UI", 5);
            string.Join("\n", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)).ShouldContain("App");
            var fileAnswer = await mcp.AnalyzeImpactFile("UI", "helpers.js", depth: 5);
            var structured = fileAnswer.StructuredContent.ShouldBeOfType<JsonElement>();
            structured.GetProperty("languageIndexed").GetBoolean().ShouldBeTrue();
            structured.GetProperty("found").GetBoolean().ShouldBeTrue();

            await File.WriteAllTextAsync(barrel, "export { second as load } from './helpers.js'; export { Two as View } from './panel.jsx';");
            await Index();
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.CALLS && edge.TargetId == firstId);
            var secondId = Id("second");
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS && edge.TargetId == secondId);
            var entry = store.Nodes.Single(node => node.QualifiedName == "UI:entry.jsx");
            var twoId = Id("Two");
            var oneId = Id("One");
            store.Edges.ShouldContain(edge => edge.SourceId == entry.Id && edge.Type == EdgeType.RENDERS && edge.TargetId == twoId);
            store.Edges.ShouldNotContain(edge => edge.SourceId == entry.Id && edge.Type == EdgeType.RENDERS && edge.TargetId == oneId);
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);

            var before = Facts();
            var hashes = await store.GetFileHashRecordsAsync("UI");
            await File.WriteAllTextAsync(Path.Combine(root, "helpers.js"), "export function broken( {");
            await Should.ThrowAsync<InvalidOperationException>(Index);
            Facts().ShouldBe(before);
            (await store.GetFileHashRecordsAsync("UI"))["helpers.js"].ContentHash.ShouldBe(hashes["helpers.js"].ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
