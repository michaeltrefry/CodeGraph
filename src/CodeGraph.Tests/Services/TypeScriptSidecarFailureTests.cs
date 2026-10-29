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
            new ExtractorContext { RepositoryName = "Lens", RootPath = "/repo" },
            ["tsconfig.json", "src/app.ts"]);

        results.ShouldHaveSingleItem().ProcessedFiles.ShouldBe(["src/app.ts"]);
        var request = JsonDocument.Parse(sidecar.ExtractBodies.ShouldHaveSingleItem()).RootElement;
        request.TryGetProperty("includedFiles", out var inline).ShouldBeTrue();
        inline.ValueKind.ShouldBe(JsonValueKind.Null);
        var scopePath = request.GetProperty("includedFilesPath").GetString().ShouldNotBeNull();
        sidecar.ScopeFileContents.ShouldHaveSingleItem().ShouldBe("""["tsconfig.json","src/app.ts"]""");
        File.Exists(scopePath).ShouldBeFalse();
    }

    /// <summary>A minimal stand-in for the Node sidecar on a free loopback port.</summary>
    private sealed class FakeSidecar : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeSidecar(WebApplication app) => _app = app;

        public int Port { get; private set; }
        public List<string> ExtractBodies { get; } = [];
        public List<string> ScopeFileContents { get; } = [];

        public static async Task<FakeSidecar> StartAsync(HttpStatusCode extractStatus, string extractBody)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var sidecar = new FakeSidecar(app);
            app.MapGet("/health", () => Results.Text("""{"status":"ok"}""", "application/json"));
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
