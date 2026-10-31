using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Extractors.TypeScript;

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
    private readonly Queue<string> _recentOutput = new();
    private int _expectedExitProcessId = -1;
    private const int MaxRecentOutputLines = 40;

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

        // If another sidecar instance is already bound to the port, reuse it.
        if (await IsServerResponsiveAsync(ct))
        {
            _available = true;
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

            if (await IsServerResponsiveAsync(ct))
            {
                _available = true;
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
            // A reused v5 process lacks bounded heritage and HTTP import lookups.
            // Require v6 before sending source paths so repeated lookup work is bounded too.
            var health = await _http.GetFromJsonAsync<JsonElement>("/health", ct);
            if (!health.TryGetProperty("sourceBoundsVersion", out var bounds)
                || !bounds.TryGetInt32(out var version) || version < 6)
            {
                _logger.LogWarning("TypeScript sidecar does not support bounded source and semantic analysis; extraction of {Repository} was refused", request.RepositoryName);
                return null;
            }
            using var response = await _http.PostAsJsonAsync("/extract-project", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // A rejected request (413 body too large, 500 extraction error) must be visible:
                // the caller falls back to per-file extraction.
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "TypeScript extractor rejected the extraction of {Repository}: HTTP {StatusCode} {Reason}: {Body}",
                    request.RepositoryName,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    body.Length > 500 ? body[..500] : body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ExtractProjectResponse>(
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.InnerException is HttpIOException)
        {
            _logger.LogWarning(
                "TypeScript extractor crashed (likely OOM) while processing {Repository}. " +
                "Process alive: {Alive}",
                request.RepositoryName, IsProcessAlive());
            _available = false;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript extractor request for {Repository} failed", request.RepositoryName);
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
            TouchLastUsed();
        }
    }

    public async Task<IReadOnlyDictionary<string, LintResult>> LintRepositoryAsync(
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
        catch (HttpRequestException ex) when (ex.InnerException is HttpIOException)
        {
            _available = false;
            _logger.LogWarning(
                "ESLint request failed because the TypeScript extractor disconnected while handling {RepoPath}. " +
                "Process alive: {Alive}. Recent output: {RecentOutput}",
                repoPath, IsProcessAlive(), string.Join(" | ", _recentOutput));
            return new Dictionary<string, LintResult>();
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
            _prerequisitesMet = FindNodeExecutable() is not null && FindServerScript() is not null;

            if (!_prerequisitesMet)
            {
                _logger.LogWarning(
                    "TypeScript extractor: neither 'node' nor 'nodejs' was found on PATH, or the server script is missing. " +
                    "TypeScript/Angular files will be skipped.");
                return false;
            }
        }

        var nodeExe = FindNodeExecutable()!;
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

        var process = Process.Start(psi);
        _process = process;
        if (process is null)
        {
            _logger.LogWarning("TypeScript extractor: failed to start Node.js process.");
            return false;
        }

        Volatile.Write(ref _expectedExitProcessId, -1);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
                _available = false;

            var expectedShutdown = Volatile.Read(ref _expectedExitProcessId) == process.Id;
            try
            {
                if (expectedShutdown)
                {
                    _logger.LogInformation(
                        "TypeScript extractor process exited after intentional shutdown. Exit code: {ExitCode}.",
                        process.ExitCode);
                }
                else
                {
                    _logger.LogWarning(
                        "TypeScript extractor process exited unexpectedly with code {ExitCode}. Recent output: {RecentOutput}",
                        process.ExitCode,
                        string.Join(" | ", _recentOutput));
                }
            }
            catch
            {
                _logger.LogWarning("TypeScript extractor process exited.");
            }
        };

        // Forward sidecar stderr to our logger so timing/diagnostic output is visible
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                RecordOutput(e.Data);
                _logger.LogInformation("ts-extractor: {Line}", e.Data);
            }
        };
        process.BeginErrorReadLine();
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                RecordOutput(e.Data);
                _logger.LogDebug("ts-extractor: {Line}", e.Data);
            }
        };
        process.BeginOutputReadLine();

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
            "TypeScript/Angular files will be skipped. Recent output: {RecentOutput}",
            string.Join(" | ", _recentOutput));
        KillProcess();
        return false;
    }

    private async Task<bool> IsServerResponsiveAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is not null)
            {
                Volatile.Write(ref _expectedExitProcessId, _process.Id);
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }
        _process = null;
    }

    private void RecordOutput(string line)
    {
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > MaxRecentOutputLines)
                _recentOutput.Dequeue();
        }
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

    private static string? FindNodeExecutable()
        => FindExecutable("node") ?? FindExecutable("nodejs");

    private static string? FindServerScript() => FindServerScript(AppContext.BaseDirectory);

    /// <summary>
    /// The sidecar's <c>dist/server.js</c>: next to the binary first (Docker / published layout),
    /// else in the repository the binary was built in. The repository root is the nearest ancestor
    /// holding a <c>.git</c> entry, a directory in a main checkout and a file in a git worktree, so
    /// a worktree build runs its own sidecar rather than the main checkout's.
    /// </summary>
    internal static string? FindServerScript(string baseDirectory)
    {
        var baseScript = Path.Combine(baseDirectory, "tools", "ts-extractor", "dist", "server.js");
        if (File.Exists(baseScript)) return baseScript;

        var dir = baseDirectory;
        while (dir is not null && !IsRepositoryRoot(dir))
            dir = Directory.GetParent(dir)?.FullName;

        if (dir is null) return null;

        var script = Path.Combine(dir, "tools", "ts-extractor", "dist", "server.js");
        return File.Exists(script) ? script : null;
    }

    private static bool IsRepositoryRoot(string directory)
    {
        var marker = Path.Combine(directory, ".git");
        return Directory.Exists(marker) || File.Exists(marker);
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
