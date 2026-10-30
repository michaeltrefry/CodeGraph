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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealSidecar_PreservesZeroLimitsAndPositiveExtensionOverrides(bool zeroDefault, bool perFile)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-zero-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.jsx"), "export function App(){return <main/>}");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            var store = new InMemoryGraphStore();
            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var options = new IndexingOptions { MaxFileSizeKb = zeroDefault ? 0 : 1,
                MaxFileSizeKbByExtension = zeroDefault ? new() { [".jsx"] = 1 } : new() { [".sql"] = 0 } };
            ICodeExtractor[] extractors = perFile ? [new TypeScriptExtractor(server, NullLogger<TypeScriptExtractor>.Instance)] : [];
            var pipeline = new IndexingPipeline(store, extractors, Options.Create(options), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: perFile ? null : analyzer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipeline.IndexRepositoryAsync("ZeroLimits", root, ct: timeout.Token);
            store.Nodes.ShouldContain(node => node.Name == "App");
            (await store.GetFileHashRecordsAsync("ZeroLimits")).Keys.ShouldContain("app.jsx");
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("ZeroLimits", root, ct: timeout.Token);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

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

    [Fact]
    public async Task RealSidecar_AccountsForAdmittedCypressAndE2eSources_WithoutReadingExcludedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-admitted-tests-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"codegraph-ui-excluded-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            async Task Write(string relative, string content)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }
            const string repository = "BrowserTests";
            string[] sources = ["cypress/e2e/login.cy.ts", "e2e/app.e2e-spec.ts", "src/Panel#fragment.ts", "src/UPPER.TS", "src/UpperPanel.JSX", "src/app.jsx", "src/deps.ts"];
            await Write("package.json", """{"name":"browser-scope","dependencies":{"react":"*"}}""");
            await Write("src/app.jsx", "import {login,LoginScreen} from '../cypress/e2e/login.cy';import {HashPanel} from './Panel#fragment';import {upper} from './UPPER.TS';import {UpperPanel} from './UpperPanel.JSX';export function App(){const handler=()=>login();login();handler();upper();return <><LoginScreen/><HashPanel/><UpperPanel/><button onClick={handler}/></>;}");
            await Write("cypress/e2e/login.cy.ts", "export function login(){return true;}export function LoginScreen(){return null;}");
            await Write("src/Panel#fragment.ts", "export function HashPanel(){return null;}");
            await Write("src/deps.ts", "export class Service{}export class Consumer{constructor(private first:Service,private second:Service){}}");
            await Write("src/UPPER.TS", "export function upper(){return 1;}");
            await Write("src/UpperPanel.JSX", "export function UpperPanel(){return <aside/>;}");
            await Write("e2e/app.e2e-spec.ts", "import {login} from '../cypress/e2e/login.cy';export function browserFlow(){return login();}");
            await Write(".gitignore", "ignored/\noutside-link.ts\n");
            await Write("ignored/invalid.ts", "export const invalid=(;");
            await Write("node_modules/private/invalid.ts", "export const invalid=(;");
            await Write(".claude/worktrees/agent/invalid.ts", "export const invalid=(;");
            await File.WriteAllTextAsync(Path.Combine(outside, "invalid.ts"), "export const invalid=(;");
            try { File.CreateSymbolicLink(Path.Combine(root, "outside-link.ts"), Path.Combine(outside, "invalid.ts")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            GitWorkingCopyDiscoveryTests.Git(root, "init", "-q");
            GitWorkingCopyDiscoveryTests.Git(root, "add", "--force", "package.json", ".gitignore", "src/app.jsx",
                "cypress/e2e/login.cy.ts", "e2e/app.e2e-spec.ts", "src/Panel#fragment.ts", "src/UPPER.TS", "src/UpperPanel.JSX", "src/deps.ts", "node_modules/private/invalid.ts", ".claude/worktrees/agent/invalid.ts");
            GitWorkingCopyDiscoveryTests.Commit(root);
            await Write(".git/invalid.ts", "export const invalid=(;");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var server = new TypeScriptServerManager(port, NullLogger.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            (await server.EnsureStartedAsync(timeout.Token)).ShouldBeTrue();
            var response = await server.ExtractProjectAsync(new ExtractProjectRequest
            {
                RepositoryName = repository,
                RootPath = root,
                TsconfigPath = Path.Combine(root, "tsconfig.json"),
                IncludedFiles = ["package.json", .. sources]
            }, timeout.Token);
            response.ShouldNotBeNull();
            response.Complete.ShouldBeTrue();
            response.ProcessedFiles.Order(StringComparer.Ordinal).ShouldBe(sources);
            response.Edges.ShouldContain(edge => edge.SourceQN == $"{repository}:src/app.jsx#App"
                && edge.TargetQN == $"{repository}:cypress/e2e/login.cy.ts#login" && edge.Type == "CALLS");
            response.Edges.ShouldContain(edge => edge.SourceQN == $"{repository}:src/app.jsx#App"
                && edge.TargetQN == $"{repository}:cypress/e2e/login.cy.ts#LoginScreen" && edge.Type == "RENDERS");
            var callback = response.Edges.Where(edge => edge.SourceQN == $"{repository}:src/app.jsx#App"
                && edge.TargetQN == $"{repository}:src/app.jsx#App.handler" && edge.Type == "CALLS").ShouldHaveSingleItem();
            callback.Properties!["extraction_evidence"].ShouldBeOfType<JsonElement>().GetArrayLength().ShouldBe(2);
            var injection = response.Edges.Where(edge => edge.SourceQN == "deps.Consumer"
                && edge.TargetQN == "deps.Service" && edge.Type == "INJECTS").ShouldHaveSingleItem();
            injection.Properties!["extraction_evidence"].ShouldBeOfType<JsonElement>().GetArrayLength().ShouldBe(2);

            var analyzer = new TypeScriptProjectAnalyzer(server, NullLogger<TypeScriptProjectAnalyzer>.Instance);
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(store, [], Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
            Task Index() => pipeline.IndexRepositoryAsync(repository, root, ct: timeout.Token);
            await Index(); // The required complete-source coverage guard remains in force.
            var hashes = await store.GetFileHashRecordsAsync(repository);
            hashes.Keys.Order(StringComparer.Ordinal).ShouldBe(sources);
            var versions = new ExtractionVersionCatalog([], typeScriptAnalyzer: analyzer);
            foreach (var source in sources)
            {
                hashes[source].ExtractionVersion.ShouldBe(versions.ForPath(source));
                hashes[source].ExtractionVersion!.ShouldContain($"{nameof(TypeScriptProjectAnalyzer)}={analyzer.ExtractionVersion}");
                hashes[source].ContentHash.ShouldBe(Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(await File.ReadAllBytesAsync(Path.Combine(root, source)))));
                store.Nodes.ShouldContain(node => node.Label == NodeLabel.File && node.FilePath == source);
            }
            var app = store.Nodes.Single(node => node.Name == "App" && node.Label == NodeLabel.Component);
            var login = store.Nodes.Single(node => node.Name == "login" && node.FilePath == "cypress/e2e/login.cy.ts");
            var loginScreen = store.Nodes.Single(node => node.Name == "LoginScreen" && node.Label == NodeLabel.Component);
            var hashPanel = store.Nodes.Single(node => node.Name == "HashPanel" && node.Label == NodeLabel.Component);
            var upper = store.Nodes.Single(node => node.Name == "upper" && node.FilePath == "src/UPPER.TS");
            var upperPanel = store.Nodes.Single(node => node.Name == "UpperPanel" && node.Label == NodeLabel.Component && node.FilePath == "src/UpperPanel.JSX");
            store.Nodes.ShouldNotContain(node => node.FilePath != null &&
                (node.FilePath.StartsWith("ignored/") || node.FilePath.StartsWith("node_modules/") ||
                 node.FilePath.StartsWith(".claude/") || node.FilePath.StartsWith(".git/") || node.FilePath == "outside-link.ts"));
            var handler = store.Nodes.Single(node => node.Name == "handler" && node.FilePath == "src/app.jsx");
            var consumer = store.Nodes.Single(node => node.Name == "Consumer" && node.Label == NodeLabel.Class);
            var service = store.Nodes.Single(node => node.Name == "Service" && node.Label == NodeLabel.Class);
            store.Edges.Where(edge => edge.SourceId == app.Id && edge.TargetId == handler.Id && edge.Type == EdgeType.CALLS).ShouldHaveSingleItem();
            store.Edges.Where(edge => edge.SourceId == consumer.Id && edge.TargetId == service.Id && edge.Type == EdgeType.INJECTS).ShouldHaveSingleItem();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await Index();
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            store.Edges.ShouldContain(edge => edge.SourceId == app.Id && edge.TargetId == login.Id && edge.Type == EdgeType.CALLS);
            store.Edges.ShouldContain(edge => edge.SourceId == app.Id && edge.TargetId == loginScreen.Id && edge.Type == EdgeType.RENDERS);
            store.Edges.ShouldContain(edge => edge.SourceId == app.Id && edge.TargetId == hashPanel.Id && edge.Type == EdgeType.RENDERS);
            store.Edges.ShouldContain(edge => edge.SourceId == app.Id && edge.TargetId == upper.Id && edge.Type == EdgeType.CALLS);
            store.Edges.ShouldContain(edge => edge.SourceId == app.Id && edge.TargetId == upperPanel.Id && edge.Type == EdgeType.RENDERS);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

}
