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

    public const string Usage =
        "usage: cg brief [--budget N] [--item KEY] | cg inbox | cg end [--item KEY] [--done TEXT] [--next TEXT] [--decision TEXT]... " +
        "[--open TEXT]... [--correction TEXT]... [--branch NAME] [--commit SHA]\n" +
        "environment: CODEGRAPH_URL (API base URL), CODEGRAPH_TOKEN (personal access token), CODEGRAPH_PROJECT, CODEGRAPH_HARNESS, " +
        "CODEGRAPH_MODEL, CODEGRAPH_INTENT, CODEGRAPH_REPOSITORY, CODEGRAPH_BRANCH, CODEGRAPH_SESSION_ID, CODEGRAPH_SESSION_FILE";

    private string? Env(string name) => environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            (args.Length == 0 ? error : output).WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

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

        using var deadline = new CancellationTokenSource(Deadline);
        var lines = new List<string>();
        try
        {
            await (options.Command switch
            {
                "brief" => BriefAsync(options, lines, deadline.Token),
                "inbox" => InboxAsync(lines, deadline.Token),
                _ => EndAsync(options, lines, deadline.Token)
            });
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

    private string Unreachable(Exception exception)
    {
        var reason = exception is TaskCanceledException or OperationCanceledException
            ? $"no answer within {Deadline.TotalSeconds:0} s"
            : exception.InnerException?.Message ?? exception.Message;
        return $"codegraph: unreachable at {Env("CODEGRAPH_URL")} ({OneLine(reason)})";
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

            // An ended, unknown or foreign session: start a new one below. Anything else is reported.
            if (status is not (400 or 404))
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

    // S2 has no inbox; the briefing carries an inbox section once messages exist (S7), and
    // cg inbox prints it. Without a session or an inbox it prints nothing.
    private async Task InboxAsync(List<string> lines, CancellationToken ct)
    {
        using var http = Client(lines);
        if (http is null || ReadSessionId() is not { } sessionId)
            return;

        var (status, body) = await SendAsync(http, HttpMethod.Get, $"api/work/sessions/{Uri.EscapeDataString(sessionId)}/briefing", null, ct);
        if (status != 200)
            throw ApiException.From(status, body);
        lines.AddRange(CgRender.Inbox(body));
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

    private string SessionFile() =>
        Env("CODEGRAPH_SESSION_FILE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codegraph", "sessions",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Directory.GetCurrentDirectory())))[..16]);

    private string? ReadSessionId()
    {
        if (Env("CODEGRAPH_SESSION_ID") is { } fromEnvironment)
            return fromEnvironment;
        try
        {
            var file = SessionFile();
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } id ? id : null;
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
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, sessionId + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the file the next brief starts another session; nothing else depends on it.
        }
    }

    private void DeleteSessionFile()
    {
        try
        {
            File.Delete(SessionFile());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string DetectHarness() =>
        Env("CLAUDECODE") is not null ? "claude-code"
        : environment.Keys.Any(name => name.StartsWith("CODEX_", StringComparison.Ordinal)) ? "codex"
        : "shell";

    // The repository (top-level folder name), branch and worktree from git, within the deadline.
    private static async Task<(string? Repository, string? Branch, string? Worktree)> ProbeGitAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse --show-toplevel --abbrev-ref HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
                return (null, null, null);

            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                var text = await process.StandardOutput.ReadToEndAsync(probe.Token);
                await process.WaitForExitAsync(probe.Token);
                if (process.ExitCode != 0)
                    return (null, null, null);
                var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var top = parts.Length > 0 ? parts[0] : null;
                var branch = parts.Length > 1 && parts[1] != "HEAD" ? parts[1] : null;
                return (top is null ? null : Path.GetFileName(top), branch, top);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

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
