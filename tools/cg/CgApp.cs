using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Cg;

/// <summary>
/// The cg command line (docs/design/agent-coordination.md §9): <c>cg brief</c>, <c>cg inbox</c>,
/// <c>cg end</c> over the REST surface of the work tools. Its contract with the hooks that call it:
/// it prints data, never directives; it leaves out empty sections; everything it does (git probe
/// included) finishes within the deadline (2 s); when CodeGraph cannot be reached it prints exactly
/// one line; and it never blocks the agent: the exit code is 0 except for a usage error (2).
/// </summary>
public sealed class CgApp(IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The hard budget from entry to the answer: leaves the rest of the 2 s deadline for the
    /// process start and for writing the one line. A watchdog enforces it, so a stuck DNS lookup or
    /// a handler that ignores cancellation cannot hold the agent past it.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    public const string Usage =
        "usage: cg brief [--budget N] [--item KEY] | cg inbox [--ack] | cg end [--item KEY] [--done TEXT] [--next TEXT] [--decision TEXT]... " +
        "[--open TEXT]... [--correction TEXT]... [--branch NAME] [--commit SHA]\n" +
        "environment: CODEGRAPH_URL (API base URL), CODEGRAPH_TOKEN (personal access token), CODEGRAPH_PROJECT, CODEGRAPH_HARNESS, " +
        "CODEGRAPH_MODEL, CODEGRAPH_INTENT, CODEGRAPH_REPOSITORY, CODEGRAPH_BRANCH, CODEGRAPH_SESSION_ID, CODEGRAPH_SESSION_FILE, " +
        "CODEGRAPH_HARNESS_SESSION (the harness's own session id, to keep one cg session per harness session; read from CLAUDE_CODE_SESSION_ID " +
        "or CODEX_THREAD_ID when unset), CODEGRAPH_GIT (git executable)";

    private string? Env(string name) => environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            (args.Length == 0 ? error : output).WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        // The watchdog starts here, before the arguments are read.
        var watchdog = Task.Delay(Budget);
        using var deadline = new CancellationTokenSource(Budget);
        var lines = new List<string>();
        try
        {
            var work = RunCommandAsync(args, lines, deadline.Token);
            if (await Task.WhenAny(work, watchdog) != work)
            {
                // The work did not end in time (a lookup that ignores cancellation, say): give up on
                // it, stop its git child, and say so in one line.
                deadline.Cancel();
                KillProbe();
                lines = [Unreachable(new OperationCanceledException())];
            }
            else
            {
                var exit = await work;
                if (exit != 0)
                    return exit;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            lines = [Unreachable(exception)];
        }
        catch (ApiException exception)
        {
            lines = [exception.Message];
        }
        catch (Exception exception)
        {
            // Never block the agent: anything else is one line too.
            lines = [$"codegraph: unexpected answer ({OneLine(exception.Message)})"];
        }

        // Written at once, so a timeout never leaves half a briefing behind.
        foreach (var line in lines)
            output.WriteLine(line);
        return 0;
    }

    // Runs the command; 2 for a usage error (already reported), else 0.
    private async Task<int> RunCommandAsync(string[] args, List<string> lines, CancellationToken ct)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"cg: {exception.Message}");
            error.WriteLine(Usage);
            return 2;
        }

        await (options.Command switch
        {
            "brief" => BriefAsync(options, lines, ct),
            "inbox" => InboxAsync(options, lines, ct),
            _ => EndAsync(options, lines, ct)
        });
        return 0;
    }

    private string Unreachable(Exception exception)
    {
        var reason = exception is TaskCanceledException or OperationCanceledException
            ? $"no answer within {Deadline.TotalSeconds:0} s"
            : exception.InnerException?.Message ?? exception.Message;
        return $"codegraph: unreachable at {DisplayUrl(Env("CODEGRAPH_URL"))} ({OneLine(reason)})";
    }

    // The URL without userinfo, query or fragment: a token or password in it is never echoed.
    private static string DisplayUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "CODEGRAPH_URL";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}";
    }

    // ---- Commands -------------------------------------------------------------------------------

    private async Task BriefAsync(Options options, List<string> lines, CancellationToken ct)
    {
        using var http = Client(lines);
        if (http is null)
            return;

        var query = new List<string>();
        if (options.Budget is { } budget)
            query.Add($"budget_tokens={budget}");
        if (options.Item is { } focus)
            query.Add($"item={Uri.EscapeDataString(focus)}");

        if (ReadSessionId() is { } known)
        {
            var (status, body) = await SendAsync(http, HttpMethod.Get, $"api/work/sessions/{Uri.EscapeDataString(known)}/briefing{(query.Count == 0 ? "" : "?" + string.Join('&', query))}", null, ct);
            if (status == 200)
            {
                lines.AddRange(CgRender.Briefing(body));
                return;
            }

            // An unknown, ended or foreign session (the API's session_unusable reason): start a new
            // one below. Any other refusal (an unknown --item, say) is reported and the session kept.
            if (status is not (400 or 404) || body?["reason"]?.GetValue<string>() != "session_unusable")
                throw ApiException.From(status, body);
        }

        var start = new JsonObject
        {
            ["harness"] = Env("CODEGRAPH_HARNESS") ?? DetectHarness(),
            ["host"] = Environment.MachineName
        };
        Set(start, "project", Env("CODEGRAPH_PROJECT"));
        Set(start, "model", Env("CODEGRAPH_MODEL"));
        Set(start, "intent", Env("CODEGRAPH_INTENT"));
        var (repository, branch, worktree) = await ProbeGitAsync(ct);
        Set(start, "repository", Env("CODEGRAPH_REPOSITORY") ?? repository);
        Set(start, "branch", Env("CODEGRAPH_BRANCH") ?? branch);
        Set(start, "worktree", worktree ?? Directory.GetCurrentDirectory());
        if (options.Budget is { } startBudget)
            start["budget_tokens"] = startBudget;

        var (startStatus, started) = await SendAsync(http, HttpMethod.Post, "api/work/sessions", start, ct);
        if (startStatus != 200)
            throw ApiException.From(startStatus, started);

        var sessionId = started?["session"]?["id"]?.GetValue<string>();
        if (sessionId is not null)
            WriteSessionId(sessionId);
        lines.AddRange(CgRender.Briefing(started?["briefing"]));
    }

    // The messages delivered to the session that were not acknowledged yet, one block per message
    // (the inbox tool without waiting). With --ack they are marked read once printed, so a hook
    // that injects them into the agent's context shows each message once; without it a crash
    // before the agent saw them replays them on the next call. Without a session or messages it
    // prints nothing.
    private async Task InboxAsync(Options options, List<string> lines, CancellationToken ct)
    {
        using var http = Client(lines);
        if (http is null || ReadSessionId() is not { } sessionId)
            return;

        var (status, body) = await SendAsync(http, HttpMethod.Get, $"api/work/inbox?session_id={Uri.EscapeDataString(sessionId)}&limit=20", null, ct);
        if (status != 200)
            throw ApiException.From(status, body);
        lines.AddRange(CgRender.Inbox(body));

        if (options.Ack && body?["items"] is JsonArray { Count: > 0 } && body["next_cursor"]?.GetValue<string>() is { } cursor)
        {
            var (ackStatus, acked) = await SendAsync(http, HttpMethod.Post, "api/work/ack", new JsonObject { ["session_id"] = sessionId, ["up_to"] = cursor }, ct);
            if (ackStatus != 200)
                throw ApiException.From(ackStatus, acked);
        }
    }

    private async Task EndAsync(Options options, List<string> lines, CancellationToken ct)
    {
        using var http = Client(lines);
        if (http is null)
            return;
        if (ReadSessionId() is not { } sessionId)
        {
            lines.Add("codegraph: no session to end");
            return;
        }

        var body = new JsonObject();
        Set(body, "item", options.Item);
        var handoff = new JsonObject();
        Set(handoff, "done", options.Done);
        Set(handoff, "next_action", options.Next);
        Set(handoff, "branch", options.Branch);
        Set(handoff, "commit", options.Commit);
        if (options.Decisions.Count > 0)
            handoff["decisions"] = new JsonArray(options.Decisions.Select(value => (JsonNode)value).ToArray());
        if (options.OpenIssues.Count > 0)
            handoff["open_issues"] = new JsonArray(options.OpenIssues.Select(value => (JsonNode)value).ToArray());
        if (options.Corrections.Count > 0)
            handoff["corrections"] = new JsonArray(options.Corrections.Select(value => (JsonNode)value).ToArray());
        if (handoff.Count > 0)
            body["handoff"] = handoff;

        var (status, ended) = await SendAsync(http, HttpMethod.Post, $"api/work/sessions/{Uri.EscapeDataString(sessionId)}/end", body, ct);
        if (status != 200)
            throw ApiException.From(status, ended);

        DeleteSessionFile();
        lines.AddRange(CgRender.Ended(ended));
    }

    // ---- HTTP -----------------------------------------------------------------------------------

    private HttpClient? Client(List<string> lines)
    {
        if (Env("CODEGRAPH_URL") is not { } url || !Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var baseAddress))
        {
            lines.Add("codegraph: not configured (CODEGRAPH_URL is not set to an absolute URL)");
            return null;
        }

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = baseAddress;
        http.Timeout = Deadline;
        if (Env("CODEGRAPH_TOKEN") is { } token)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<(int Status, JsonNode? Body)> SendAsync(HttpClient http, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? parsed = null;
        try
        {
            parsed = text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
        }

        return ((int)response.StatusCode, parsed);
    }

    private sealed class ApiException(string message) : Exception(message)
    {
        public static ApiException From(int status, JsonNode? body)
        {
            var code = body?["code"]?.GetValue<string>();
            var message = body?["message"]?.GetValue<string>() ?? body?["detail"]?.GetValue<string>();
            return new ApiException(code is null && message is null
                ? $"codegraph: HTTP {status}"
                : $"codegraph: {code ?? $"HTTP {status}"}: {OneLine(message ?? "")}");
        }
    }

    private static void Set(JsonObject target, string name, string? value)
    {
        if (value is not null)
            target[name] = value;
    }

    internal static string OneLine(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= 300 ? flat : flat[..299] + "…";
    }

    // ---- Session file and environment -----------------------------------------------------------

    private string Harness() => Env("CODEGRAPH_HARNESS") ?? DetectHarness();

    // The harness's own session id, so two sessions of one harness in one checkout stay apart too.
    // CLAUDE_CODE_SESSION_ID is set in Claude Code's shell, CODEX_THREAD_ID in Codex's.
    private string? HarnessSession() =>
        Env("CODEGRAPH_HARNESS_SESSION") ?? Env("CLAUDE_CODE_SESSION_ID") ?? Env("CODEX_THREAD_ID") ?? Env("CODEX_SESSION_ID");

    private string SessionFile() =>
        Env("CODEGRAPH_SESSION_FILE") ?? Path.Combine(
            Env("HOME") ?? Env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codegraph", "sessions",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Harness()}\n{HarnessSession()}\n{Directory.GetCurrentDirectory()}")))[..16]);

    // Next to the session file: the harness that started the session, so a file shared by another
    // harness is never reused for this one.
    private string HarnessFile() => SessionFile() + ".harness";

    private string? ReadSessionId()
    {
        if (Env("CODEGRAPH_SESSION_ID") is { } fromEnvironment)
            return fromEnvironment;
        try
        {
            var file = SessionFile();
            if (!File.Exists(file) || File.ReadAllText(file).Trim() is not { Length: > 0 } id)
                return null;
            var harnessFile = HarnessFile();
            if (File.Exists(harnessFile) && File.ReadAllText(harnessFile).Trim() != Harness())
                return null;
            return id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteSessionId(string sessionId)
    {
        try
        {
            var file = SessionFile();
            var directory = Path.GetDirectoryName(file)!;
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WritePrivate(file, sessionId + "\n");
            WritePrivate(HarnessFile(), Harness() + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the file the next brief starts another session; nothing else depends on it.
        }
    }

    // Owner-only (0600) on Unix, also for a file that already existed.
    private static void WritePrivate(string file, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(file, text);
            return;
        }

        using (var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void DeleteSessionFile()
    {
        try
        {
            File.Delete(SessionFile());
            File.Delete(HarnessFile());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string DetectHarness() =>
        Env("CLAUDECODE") is not null ? "claude-code"
        : environment.Keys.Any(name => name.StartsWith("CODEX_", StringComparison.Ordinal)) ? "codex"
        : "shell";

    private Process? probe;

    private void KillProbe()
    {
        try
        {
            probe?.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    // The repository (top-level folder name), branch and worktree from git, within the deadline.
    // The git child is killed whenever it has not finished by then: at its own 700 ms limit, at the
    // overall deadline or when the watchdog gives up.
    private async Task<(string? Repository, string? Branch, string? Worktree)> ProbeGitAsync(CancellationToken ct)
    {
        try
        {
            probe = Process.Start(new ProcessStartInfo(Env("CODEGRAPH_GIT") ?? "git", "rev-parse --show-toplevel --abbrev-ref HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (probe is null)
                return (null, null, null);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                var text = await probe.StandardOutput.ReadToEndAsync(limit.Token);
                await probe.WaitForExitAsync(limit.Token);
                if (probe.ExitCode != 0)
                    return (null, null, null);
                var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var top = parts.Length > 0 ? parts[0] : null;
                var branch = parts.Length > 1 && parts[1] != "HEAD" ? parts[1] : null;
                return (top is null ? null : Path.GetFileName(top), branch, top);
            }
            catch (OperationCanceledException)
            {
                KillProbe();
                // Past the overall deadline the caller gives up; at the probe's own limit the
                // brief goes on without git's answer.
                ct.ThrowIfCancellationRequested();
                return (null, null, null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, null, null);
        }
    }

    // ---- Arguments ------------------------------------------------------------------------------

    private sealed record Options(string Command)
    {
        public bool Ack { get; private set; }
        public int? Budget { get; private set; }
        public string? Item { get; private set; }
        public string? Done { get; private set; }
        public string? Next { get; private set; }
        public string? Branch { get; private set; }
        public string? Commit { get; private set; }
        public List<string> Decisions { get; } = [];
        public List<string> OpenIssues { get; } = [];
        public List<string> Corrections { get; } = [];

        public static Options Parse(string[] args)
        {
            var command = args[0];
            if (command is not ("brief" or "inbox" or "end"))
                throw new ArgumentException($"unknown command '{command}'");

            var options = new Options(command);
            var allowed = command switch
            {
                "brief" => new[] { "--budget", "--item" },
                "inbox" => [],
                _ => ["--item", "--done", "--next", "--decision", "--open", "--correction", "--branch", "--commit"]
            };
            for (var index = 1; index < args.Length; index++)
            {
                var flag = args[index];
                if (command == "inbox" && flag == "--ack")
                {
                    options.Ack = true;
                    continue;
                }

                if (!allowed.Contains(flag))
                    throw new ArgumentException($"cg {command} takes no '{flag}'");
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"{flag} needs a value");
                var value = args[++index];
                switch (flag)
                {
                    case "--budget":
                        options.Budget = int.TryParse(value, out var budget) && budget > 0 ? budget : throw new ArgumentException("--budget needs a positive number");
                        break;
                    case "--item": options.Item = value; break;
                    case "--done": options.Done = value; break;
                    case "--next": options.Next = value; break;
                    case "--branch": options.Branch = value; break;
                    case "--commit": options.Commit = value; break;
                    case "--decision": options.Decisions.Add(value); break;
                    case "--open": options.OpenIssues.Add(value); break;
                    case "--correction": options.Corrections.Add(value); break;
                }
            }

            return options;
        }
    }
}
