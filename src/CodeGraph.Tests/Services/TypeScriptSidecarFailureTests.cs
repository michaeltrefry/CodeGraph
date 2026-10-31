using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using CodeGraph.Extractors.TypeScript;
using CodeGraph.Services;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26145: a sidecar that rejects the extraction request (413 body too large on a big scope)
/// must not read as an empty repository; the cause is logged at Warning and the analysis fails so the
/// pipeline logs its per-file fallback. The scope itself travels in a temp file.
/// </summary>
public class TypeScriptSidecarFailureTests
{
    [Fact]
    public async Task AnalyzeProjectAsync_RejectedRequest_LogsTheCauseAndFails()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.RequestEntityTooLarge,
            """{"statusCode":413,"error":"Payload Too Large","message":"Request body is too large"}""");
        var managerLog = new RecordingLogger();
        using var server = new TypeScriptServerManager(sidecar.Port, managerLog);
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync(
            "/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "Lens", RootPath = "/repo" },
            ["tsconfig.json", "src/app.ts"]));

        failure.Message.ShouldContain("Lens");
        managerLog.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("413", StringComparison.Ordinal)
            && entry.Message.Contains("Request body is too large", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeProjectAsync_PassesTheScopeThroughATempFileAndDeletesIt()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"nodes":[],"edges":[],"workspacePackages":[],"resolvedImports":[],"unresolvedImports":[],"unresolvedCalls":[],"processedFiles":["src/app.ts"]}""");
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());

        var results = await analyzer.AnalyzeProjectAsync(
            "/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "Lens", RootPath = "/repo", TypeScriptSourceLimits = new()
            { MaxFileSizeKb = 7, MaxFileSizeKbByExtension = new Dictionary<string, int> { [".tsx"] = 11 }, MaxAnalysisTextKb = 19 } },
            ["tsconfig.json", "src/app.ts"]);

        results.ShouldHaveSingleItem().ProcessedFiles.ShouldBe(["src/app.ts"]);
        var request = JsonDocument.Parse(sidecar.ExtractBodies.ShouldHaveSingleItem()).RootElement;
        request.TryGetProperty("includedFiles", out var inline).ShouldBeTrue();
        inline.ValueKind.ShouldBe(JsonValueKind.Null);
        request.GetProperty("maxFileSizeKb").GetInt32().ShouldBe(7);
        request.GetProperty("maxFileSizeKbByExtension").GetProperty(".tsx").GetInt32().ShouldBe(11);
        request.GetProperty("maxAnalysisTextKb").GetInt32().ShouldBe(19);
        var scopePath = request.GetProperty("includedFilesPath").GetString().ShouldNotBeNull();
        sidecar.ScopeFileContents.ShouldHaveSingleItem().ShouldBe("""["tsconfig.json","src/app.ts"]""");
        File.Exists(scopePath).ShouldBeFalse();
    }

    [Fact]
    public async Task AnalyzeProjectAsync_IncompleteResponse_RefusesToReplacePreviousFacts()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":false,"nodes":[],"edges":[],"processedFiles":["src/good.ts"],"diagnostics":["Syntax error in broken.ts"]}""");
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync(
            "/repo/tsconfig.json", new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" },
            ["src/good.ts", "src/broken.ts"]));
        failure.Message.ShouldContain("incomplete");
        failure.Message.ShouldContain("Syntax error in broken.ts");
    }

    [Fact]
    public async Task PerFileExtractor_IncompleteResponse_DoesNotReportSuccessfulEmptyOutput()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":false,"nodes":[],"edges":[],"processedFiles":[]}""");
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var extractor = new TypeScriptExtractor(server, new RecordingLogger<TypeScriptExtractor>());
        var result = await extractor.ExtractAsync("/repo/app.ts", "const broken = ;",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" });
        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull().ShouldContain("incomplete");
    }

    [Fact]
    public async Task AnalyzerScopeMetadata_IsBoundedBeforeStartingOrSendingToSidecar()
    {
        using var server = new TypeScriptServerManager(1, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        var scope = Enumerable.Repeat("empty.js", TypeScriptSourceLimits.MaxAdmittedPaths + 1).ToList();
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, scope));
        var longPaths = Enumerable.Repeat(new string('x', 1024), TypeScriptSourceLimits.MaxPathCharacters / 1024 + 1).ToList();
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, longPaths));
    }

    [Fact]
    public async Task PerFileExtractor_RejectsOversizeBeforeTemporaryWriteOrSidecarStartup()
    {
        using var server = new TypeScriptServerManager(1, new RecordingLogger());
        var extractor = new TypeScriptExtractor(server, new RecordingLogger<TypeScriptExtractor>());
        var result = await extractor.ExtractAsync("/repo/app.tsx", new string('x', 1025),
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo", TypeScriptSourceLimits = new() { MaxFileSizeKb = 1 } });
        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull().ShouldContain("text limit");
    }

    [Fact]
    public async Task OldSidecarWithoutBoundedInputCapability_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", supportsSourceBounds: false);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V1SidecarWithoutSemanticWorkLimits_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 1);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V2SidecarWithoutReactWorkLimits_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 2);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V3SidecarWithoutEarlyAllocationLimits_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 3);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V4SidecarWithoutConfigAndSemanticRetentionLimits_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 4);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V5SidecarWithoutBoundedRepeatedLookups_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 5);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V6SidecarWithoutNominalHttpProvenance_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 6);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task V7SidecarWithoutIndexedAliasBindings_NeverReceivesSourcePaths()
    {
        await using var sidecar = await FakeSidecar.StartAsync(HttpStatusCode.OK,
            """{"complete":true,"nodes":[],"edges":[],"processedFiles":["app.js"]}""", sourceBoundsVersion: 7);
        using var server = new TypeScriptServerManager(sidecar.Port, new RecordingLogger());
        var analyzer = new TypeScriptProjectAnalyzer(server, new RecordingLogger<TypeScriptProjectAnalyzer>());
        await Should.ThrowAsync<InvalidOperationException>(() => analyzer.AnalyzeProjectAsync("/repo/tsconfig.json",
            new ExtractorContext { RepositoryName = "UI", RootPath = "/repo" }, ["app.js"]));
        sidecar.ExtractBodies.ShouldBeEmpty();
    }

    /// <summary>A minimal stand-in for the Node sidecar on a free loopback port.</summary>
    private sealed class FakeSidecar : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeSidecar(WebApplication app) => _app = app;

        public int Port { get; private set; }
        public List<string> ExtractBodies { get; } = [];
        public List<string> ScopeFileContents { get; } = [];

        public static async Task<FakeSidecar> StartAsync(HttpStatusCode extractStatus, string extractBody, bool supportsSourceBounds = true, int sourceBoundsVersion = 8)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var sidecar = new FakeSidecar(app);
            app.MapGet("/health", () => Results.Text(supportsSourceBounds
                ? JsonSerializer.Serialize(new { status = "ok", sourceBoundsVersion }) : """{"status":"ok"}""", "application/json"));
            app.MapPost("/extract-project", async (HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                sidecar.ExtractBodies.Add(body);
                if (JsonDocument.Parse(body).RootElement.TryGetProperty("includedFilesPath", out var scope)
                    && scope.GetString() is { } scopePath && File.Exists(scopePath))
                {
                    sidecar.ScopeFileContents.Add(await File.ReadAllTextAsync(scopePath));
                }

                return Results.Text(extractBody, "application/json", Encoding.UTF8, (int)extractStatus);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            sidecar.Port = new Uri(address).Port;
            return sidecar;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;
}
