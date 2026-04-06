using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Services.Analyzers;

namespace TC.CodeGraphApi.Extractors.TypeScript;

/// <summary>
/// Manages the lifecycle of the Node.js ts-extractor sidecar process.
/// Auto-starts on first use, restarts if the process dies, and shuts down
/// after an idle period with no requests.
/// </summary>
public sealed class TypeScriptServerManager : ILintRunner, IDisposable, IAsyncDisposable
{
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);

    private Process? _process;
    private bool _available;
    private bool _checkedPrerequisites;
    private bool _prerequisitesMet;
    private DateTime _lastUsedUtc = DateTime.MinValue;
    private CancellationTokenSource? _idleCts;
    private int _activeRequests;

    public TypeScriptServerManager(int port, ILogger logger)
    {
        _port = port;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public bool IsAvailable => _available;

    /// <summary>
    /// Ensures the Node.js server is running. Safe to call concurrently.
    /// Restarts the server if the process has died since last use.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        // Fast path: server is running and process is alive
        if (_available && IsProcessAlive())
        {
            TouchLastUsed();
            return true;
        }

        await _startLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_available && IsProcessAlive())
            {
                TouchLastUsed();
                return true;
            }

            // Check prerequisites once (node + script exist)
            if (_checkedPrerequisites && !_prerequisitesMet)
                return false;

            // Kill stale process if any
            KillProcess();
            _available = false;

            _available = await StartServerAsync(ct);
            if (_available)
                StartIdleMonitor();

            return _available;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<ExtractProjectResponse?> ExtractProjectAsync(
        ExtractProjectRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _activeRequests);
        try
        {
            TouchLastUsed();
            var response = await _http.PostAsJsonAsync("/extract-project", request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ExtractProjectResponse>(
                cancellationToken: ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is HttpIOException)
        {
            _logger.LogWarning(
                "TypeScript extractor crashed (likely OOM) while processing {Project}. " +
                "Process alive: {Alive}",
                request.ProjectName, IsProcessAlive());
            _available = false;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript extractor request failed");
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
            TouchLastUsed();
        }
    }

    public async Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(
        string repoPath, CancellationToken ct = default)
    {
        if (!await EnsureStartedAsync(ct))
            return new Dictionary<string, LintResult>();

        Interlocked.Increment(ref _activeRequests);
        try
        {
            TouchLastUsed();
            var request = new LintProjectRequest { RepoPath = repoPath };
            var response = await _http.PostAsJsonAsync("/lint-project", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LintProjectResponse>(
                cancellationToken: ct);

            if (result?.Diagnostics is { Count: > 0 })
            {
                foreach (var diag in result.Diagnostics)
                    _logger.LogDebug("ts-extractor lint: {Diagnostic}", diag);
            }

            var dict = new Dictionary<string, LintResult>(StringComparer.OrdinalIgnoreCase);
            if (result?.Results is not null)
            {
                foreach (var r in result.Results)
                    dict[r.FilePath] = new LintResult(r.ErrorCount, r.WarningCount);
            }

            return dict;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ESLint request failed for {RepoPath}", repoPath);
            return new Dictionary<string, LintResult>();
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
            TouchLastUsed();
        }
    }

    private void TouchLastUsed() => _lastUsedUtc = DateTime.UtcNow;

    private bool IsProcessAlive()
    {
        try { return _process is not null && !_process.HasExited; }
        catch { return false; }
    }

    private void StartIdleMonitor()
    {
        _idleCts?.Cancel();
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                if (_activeRequests > 0) continue; // request in flight — not idle
                if (DateTime.UtcNow - _lastUsedUtc > _idleTimeout)
                {
                    _logger.LogInformation(
                        "TypeScript extractor idle for {Minutes}m — shutting down",
                        (int)_idleTimeout.TotalMinutes);
                    KillProcess();
                    _available = false;
                    return;
                }
            }
        }, token);
    }

    private async Task<bool> StartServerAsync(CancellationToken ct)
    {
        if (!_checkedPrerequisites)
        {
            _checkedPrerequisites = true;
            _prerequisitesMet = FindExecutable("node") is not null && FindServerScript() is not null;

            if (!_prerequisitesMet)
            {
                _logger.LogWarning(
                    "TypeScript extractor: 'node' not found on PATH or server script missing. " +
                    "TypeScript/Angular files will be skipped.");
                return false;
            }
        }

        var nodeExe = FindExecutable("node")!;
        var scriptPath = FindServerScript()!;

        _logger.LogInformation(
            "Starting TypeScript extractor server (port {Port})...", _port);

        var psi = new ProcessStartInfo(nodeExe, $"--max-old-space-size=4096 \"{scriptPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["CODEGRAPH_TS_PORT"] = _port.ToString();

        _process = Process.Start(psi);
        if (_process is null)
        {
            _logger.LogWarning("TypeScript extractor: failed to start Node.js process.");
            return false;
        }

        // Forward sidecar stderr to our logger so timing/diagnostic output is visible
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("ts-extractor: {Line}", e.Data);
        };
        _process.BeginErrorReadLine();

        // Kill child when .NET process exits
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillProcess();

        // Poll /health until ready (10s deadline)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var resp = await _http.GetAsync("/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("TypeScript extractor server ready");
                    TouchLastUsed();
                    return true;
                }
            }
            catch { /* not ready yet */ }

            await Task.Delay(250, ct);
        }

        _logger.LogWarning(
            "TypeScript extractor server did not become ready within 10 seconds. " +
            "TypeScript/Angular files will be skipped.");
        KillProcess();
        return false;
    }

    private void KillProcess()
    {
        try { _process?.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _process = null;
    }

    private static string? FindExecutable(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (var ext in extensions)
            {
                var full = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static string? FindServerScript()
    {
        // Check relative to the app base directory first (Docker / published layout)
        var baseScript = Path.Combine(AppContext.BaseDirectory, "tools", "ts-extractor", "dist", "server.js");
        if (File.Exists(baseScript)) return baseScript;

        // Walk up from the running assembly to find the repo root (.git marker)
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;

        if (dir is null) return null;

        var script = Path.Combine(dir, "tools", "ts-extractor", "dist", "server.js");
        return File.Exists(script) ? script : null;
    }

    public void Dispose()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        KillProcess();
        _http.Dispose();
        _startLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
