using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

/// <summary>
/// Where the Shortcut importer reads from. Read only: no implementation writes to Shortcut. The REST
/// one reads Shortcut's API v3; the recorded one reads captured payloads (tests, the staging run).
/// </summary>
public interface IShortcutSource
{
    Task<IReadOnlyList<ShortcutTeam>> ListTeamsAsync(CancellationToken ct);
    Task<IReadOnlyList<ShortcutMember>> ListMembersAsync(CancellationToken ct);
    Task<IReadOnlyList<ShortcutState>> ListStatesAsync(CancellationToken ct);

    /// <summary>Every epic, archived ones included (their comments may be left out).</summary>
    Task<IReadOnlyList<ShortcutEpic>> ListEpicsAsync(CancellationToken ct);

    /// <summary>The epic with its comments.</summary>
    Task<ShortcutEpic?> GetEpicAsync(long epicId, CancellationToken ct);

    Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(long epicId, CancellationToken ct);

    /// <summary>
    /// The non-archived stories; with <paramref name="updatedSince"/> (a delta) those updated at or after it, archived
    /// ones included, so an imported story archived since reaches the import (sc-27929).
    /// </summary>
    Task<IReadOnlyList<ShortcutStorySummary>> ListStoriesAsync(DateTime? updatedSince, CancellationToken ct);

    /// <summary>How many stories are archived (reported as skipped).</summary>
    Task<int> CountArchivedStoriesAsync(CancellationToken ct);

    Task<ShortcutStory?> GetStoryAsync(long storyId, CancellationToken ct);

    /// <summary>The file's bytes, at most <paramref name="maxBytes"/>; null when it is larger. A failed download throws.</summary>
    Task<byte[]?> DownloadFileAsync(string url, long maxBytes, CancellationToken ct);

    /// <summary>What the source could not read completely (a search window over Shortcut's result cap).</summary>
    IReadOnlyList<string> Warnings { get; }
}

/// <summary>Spaces requests to at most N a minute (Shortcut allows 200) and backs off on 429 and 5xx.</summary>
public sealed class RequestRateLimiter(int requestsPerMinute, TimeProvider? clock = null)
{
    private readonly TimeSpan interval = TimeSpan.FromMilliseconds(60_000.0 / Math.Max(1, requestsPerMinute));
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private DateTimeOffset next = DateTimeOffset.MinValue;

    public int Requests { get; private set; }

    public async Task WaitAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (next > now)
                await Task.Delay(next - now, time, ct);
            next = (next > now ? next : now) + interval;
            Requests++;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Pushes the next request out (a 429's Retry-After).</summary>
    public void Pause(TimeSpan delay)
    {
        var until = time.GetUtcNow() + delay;
        if (until > next)
            next = until;
    }
}

/// <summary>Shortcut's REST API v3, authenticated with the MCP hub's shared Shortcut token (shortcut/apiToken).</summary>
public sealed class ShortcutRestSource(HttpClient http, string token, RequestRateLimiter limiter) : IShortcutSource
{
    public const string HttpClientName = "work-import-shortcut";
    private const int MaxAttempts = 6;
    // Shortcut's story search answers at most this many results for one query; larger windows are split.
    private const int SearchCap = 1000;

    public static async Task<ShortcutRestSource> CreateAsync(IHttpClientFactory factory, IMcpHubStore hub, IOptions<WorkImportOptions> options, CancellationToken ct)
    {
        // The same credential path as the hub's shared Shortcut mode (McpHubService.GetSharedShortcutTokenAsync).
        var token = await hub.GetCredentialValueAsync("shortcut", "apiToken", ct);
        if (string.IsNullOrWhiteSpace(token))
            token = await hub.GetCredentialValueAsync("shortcut-shim", "discoveryToken", ct);
        if (string.IsNullOrWhiteSpace(token))
            throw WorkException.Invalid("No Shortcut API token is configured. Set shortcut/apiToken in MCP Hub credentials (Admin > MCP Hub) before running the Shortcut import.");

        var client = factory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(options.Value.ShortcutBaseUrl);
        return new ShortcutRestSource(client, token.Trim(), new RequestRateLimiter(options.Value.ShortcutRequestsPerMinute));
    }

    public int Requests => limiter.Requests;

    private readonly List<string> warnings = [];

    public IReadOnlyList<string> Warnings => warnings;

    /// <summary>A Shortcut host: shortcut.com or a subdomain of it. The token is sent to nothing else.</summary>
    public static bool IsShortcutHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("shortcut.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".shortcut.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a redirect target is a public host: no loopback, private, link-local or otherwise internal address.</summary>
    public static async Task<bool> IsPublicHostAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    /// <summary>Whether an address is public: not loopback, private, link-local, shared, benchmark, multicast or a translation prefix.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
                return false;
            // 64:ff9b::/96 (NAT64, reaches IPv4 inside) and 2002::/16 (6to4, embeds an IPv4 address).
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(b => b == 0))
                return false;
            return !(bytes[0] == 0x20 && bytes[1] == 0x02);
        }

        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] >= 64 && bytes[1] <= 127 => false, // 100.64.0.0/10, carrier-grade NAT
            169 when bytes[1] == 254 => false,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => false,
            192 when bytes[1] == 168 => false,
            192 when bytes[1] == 0 && bytes[2] == 0 => false, // 192.0.0.0/24, IETF protocol assignments
            198 when bytes[1] is 18 or 19 => false, // 198.18.0.0/15, benchmarking
            >= 224 => false,
            _ => true
        };
    }

    /// <summary>
    /// The connection of the import's HTTP client (SocketsHttpHandler.ConnectCallback): resolves the host
    /// and connects only to a public address, checked on the address actually connected to, so a DNS
    /// answer that changes between a check and the connection (rebinding) cannot reach an internal host.
    /// </summary>
    public static async ValueTask<Stream> ConnectPublicAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var allowed = addresses.Where(IsPublicAddress).ToList();
        if (allowed.Count == 0)
            throw new HttpRequestException($"Refusing to connect to {endpoint.Host}: it resolves to no public address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), endpoint.Port, ct);
            if (socket.RemoteEndPoint is not IPEndPoint connected || !IsPublicAddress(connected.Address))
                throw new HttpRequestException($"Refusing the connection to {endpoint.Host}: not a public address.");
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The primary handler of the import's Shortcut client: no automatic redirects, public addresses only.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = (context, ct) => ConnectPublicAsync(context.DnsEndPoint, ct)
    };

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct, bool allowNotFound = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            await limiter.WaitAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var target = request.RequestUri!.IsAbsoluteUri ? request.RequestUri : new Uri(http.BaseAddress!, request.RequestUri);
            if (!IsShortcutHost(target))
                throw new HttpRequestException($"Refusing to send the Shortcut token to {target.Host}.");
            request.Headers.Add("Shortcut-Token", token);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return document.RootElement.Clone();
            }

            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            var retriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retriable || attempt == MaxAttempts)
                throw new HttpRequestException($"Shortcut answered {(int)response.StatusCode} {response.ReasonPhrase} for GET {path}.", null, response.StatusCode);

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            limiter.Pause(delay);
        }
    }

    public async Task<IReadOnlyList<ShortcutTeam>> ListTeamsAsync(CancellationToken ct) =>
        (await GetJsonAsync("groups", ct))!.Value.EnumerateArray().Select(ShortcutPayload.Team).ToList();

    public async Task<IReadOnlyList<ShortcutMember>> ListMembersAsync(CancellationToken ct) =>
        (await GetJsonAsync("members", ct))!.Value.EnumerateArray().Select(ShortcutPayload.Member).ToList();

    public async Task<IReadOnlyList<ShortcutState>> ListStatesAsync(CancellationToken ct) =>
        ShortcutPayload.States((await GetJsonAsync("workflows", ct))!.Value).ToList();

    public async Task<IReadOnlyList<ShortcutEpic>> ListEpicsAsync(CancellationToken ct) =>
        (await GetJsonAsync("epics?includes_description=true", ct))!.Value.EnumerateArray().Select(ShortcutPayload.Epic).ToList();

    public async Task<ShortcutEpic?> GetEpicAsync(long epicId, CancellationToken ct) =>
        await GetJsonAsync($"epics/{epicId}", ct, allowNotFound: true) is { } epic ? ShortcutPayload.Epic(epic) : null;

    public async Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(long epicId, CancellationToken ct)
    {
        if (await GetJsonAsync($"epics/{epicId}/documents", ct, allowNotFound: true) is not { } listed)
            return [];
        var list = listed.ValueKind == JsonValueKind.Array ? listed : listed.TryGetProperty("data", out var data) ? data : default;
        if (list.ValueKind != JsonValueKind.Array)
            return [];
        var documents = new List<ShortcutDocument>();
        foreach (var summary in list.EnumerateArray())
        {
            if (ShortcutPayload.Str(summary, "id") is not { } id)
                continue;
            if (await GetJsonAsync($"documents/{Uri.EscapeDataString(id)}", ct, allowNotFound: true) is { } full)
                documents.Add(ShortcutPayload.Document(full));
        }

        return documents;
    }

    public async Task<IReadOnlyList<ShortcutStorySummary>> ListStoriesAsync(DateTime? updatedSince, CancellationToken ct)
    {
        var found = new Dictionary<long, ShortcutStorySummary>();
        // Search filters by whole days: a day earlier, so nothing updated on the boundary day (in any
        // time zone) is missed; the exact bound is applied below.
        var filter = updatedSince is { } since ? $" updated:{since.AddDays(-1):yyyy-MM-dd}..*" : "";
        var from = new DateOnly(2000, 1, 1);
        var to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await CollectAsync(from, to, filter, archived: false, found, ct);
        // A delta also reads the stories archived since (sc-27929): an imported one is closed. A full import
        // skips archived stories; the importer re-reads the imported ones it no longer lists.
        if (updatedSince is not null)
            await CollectAsync(from, to, filter, archived: true, found, ct);
        return found.Values
            // Search filters by day; the exact bound is applied here.
            .Where(story => updatedSince is null || story.UpdatedAt >= updatedSince.Value)
            .OrderBy(story => story.Id).ToList();
    }

    private async Task CollectAsync(DateOnly from, DateOnly to, string filter, bool archived, Dictionary<long, ShortcutStorySummary> found, CancellationToken ct)
    {
        var query = $"{(archived ? "" : "!")}is:archived created:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}{filter}";
        // Shortcut's search pages hold at most 25 results.
        var path = $"search/stories?detail=slim&page_size=25&query={Uri.EscapeDataString(query)}";
        var first = (await GetJsonAsync(path, ct))!.Value;
        var total = (int)(ShortcutPayload.Long(first, "total") ?? 0);
        if (total > SearchCap && to.DayNumber == from.DayNumber)
            warnings.Add($"More than {SearchCap} stories ({total}) were created on {from:yyyy-MM-dd}: Shortcut's search answers at most {SearchCap}, so some of them were not read. Run the import again with a narrower updated_since, or read that day's stories by another filter.");
        if (total > SearchCap && to.DayNumber - from.DayNumber > 0)
        {
            var middle = DateOnly.FromDayNumber(from.DayNumber + (to.DayNumber - from.DayNumber) / 2);
            await CollectAsync(from, middle, filter, archived, found, ct);
            await CollectAsync(middle.AddDays(1), to, filter, archived, found, ct);
            return;
        }

        var page = first;
        while (true)
        {
            if (page.TryGetProperty("data", out var data))
                foreach (var story in data.EnumerateArray())
                    if (ShortcutPayload.Long(story, "id") is { } id && ShortcutPayload.Bool(story, "archived") == archived)
                        found[id] = new ShortcutStorySummary(id, ShortcutPayload.Time(story, "updated_at") ?? DateTime.UnixEpoch, archived);
            if (ShortcutPayload.Str(page, "next") is not { Length: > 0 } next)
                return;
            // next is a path with the API prefix (/api/v3/search/stories?...&next=...).
            var relative = next.StartsWith("/api/v3/", StringComparison.Ordinal) ? next["/api/v3/".Length..] : next.TrimStart('/');
            page = (await GetJsonAsync(relative, ct))!.Value;
        }
    }

    public async Task<int> CountArchivedStoriesAsync(CancellationToken ct)
    {
        var page = (await GetJsonAsync($"search/stories?detail=slim&page_size=1&query={Uri.EscapeDataString("is:archived")}", ct))!.Value;
        return (int)(ShortcutPayload.Long(page, "total") ?? 0);
    }

    public async Task<ShortcutStory?> GetStoryAsync(long storyId, CancellationToken ct) =>
        await GetJsonAsync($"stories/{storyId}", ct, allowNotFound: true) is { } story ? ShortcutPayload.Story(story) : null;

    public async Task<byte[]?> DownloadFileAsync(string url, long maxBytes, CancellationToken ct)
    {
        var target = new Uri(url);
        var authenticated = IsShortcutHost(target);
        for (var attempt = 1; ; attempt++)
        {
            await limiter.WaitAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            // The token goes to Shortcut hosts only: not to a file URL elsewhere, nor to the storage
            // host a redirect names.
            if (authenticated && IsShortcutHost(target))
                request.Headers.Add("Shortcut-Token", token);
            else if (!await IsPublicHostAsync(target, ct))
                throw new HttpRequestException($"Refusing to download a file from {target.Host}: not a public host.");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && attempt < MaxAttempts)
            {
                target = location.IsAbsoluteUri ? location : new Uri(target, location);
                if (!await IsPublicHostAsync(target, ct))
                    throw new HttpRequestException($"Refusing to follow a redirect to {target.Host}: not a public host.");
                authenticated = IsShortcutHost(target);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var retriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retriable || attempt == MaxAttempts)
                    throw new HttpRequestException($"Shortcut answered {(int)response.StatusCode} for a file download.", null, response.StatusCode);
                limiter.Pause(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));
                continue;
            }

            if (response.Content.Headers.ContentLength > maxBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await ReadBoundedAsync(stream, maxBytes, ct);
        }
    }

    internal static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// Recorded Shortcut payloads: a directory with <c>teams.json</c>, <c>members.json</c>,
/// <c>workflows.json</c>, <c>epics/*.json</c>, <c>stories/*.json</c>, <c>documents/EPIC.json</c>
/// (an array) and <c>files/</c> (bytes named by the file's id), or the same built in memory.
/// </summary>
public sealed class RecordedShortcutSource : IShortcutSource
{
    public List<ShortcutTeam> Teams { get; } = [];
    public List<ShortcutMember> Members { get; } = [];
    public List<ShortcutState> States { get; } = [];
    public Dictionary<long, ShortcutEpic> Epics { get; } = [];
    public Dictionary<long, List<ShortcutDocument>> Documents { get; } = [];
    public Dictionary<long, ShortcutStory> Stories { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public int StoryReads { get; private set; }

    public IReadOnlyList<string> Warnings { get; } = [];

    public static RecordedShortcutSource FromDirectory(string directory)
    {
        var source = new RecordedShortcutSource();
        JsonElement Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }

        IEnumerable<JsonElement> List(JsonElement element, string wrapper) =>
            element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : element.GetProperty(wrapper).EnumerateArray();

        source.Teams.AddRange(List(Read(Path.Combine(directory, "teams.json")), "teams").Select(ShortcutPayload.Team));
        var members = Read(Path.Combine(directory, "members.json"));
        source.Members.AddRange((members.ValueKind == JsonValueKind.Array ? members.EnumerateArray() : members.EnumerateObject().Select(p => p.Value)).Select(ShortcutPayload.Member));
        source.States.AddRange(ShortcutPayload.States(Read(Path.Combine(directory, "workflows.json"))));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "epics"), "*.json"))
        {
            var epic = ShortcutPayload.Epic(Read(path));
            source.Epics[epic.Id] = epic;
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "stories"), "*.json"))
        {
            var story = ShortcutPayload.Story(Read(path));
            source.Stories[story.Id] = story;
        }

        var documents = Path.Combine(directory, "documents");
        if (Directory.Exists(documents))
            foreach (var path in Directory.EnumerateFiles(documents, "*.json"))
                source.Documents[long.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture)] =
                    Read(path).EnumerateArray().Select(ShortcutPayload.Document).ToList();

        var files = Path.Combine(directory, "files");
        if (Directory.Exists(files))
            foreach (var story in source.Stories.Values)
                foreach (var file in story.Files)
                    if (File.Exists(Path.Combine(files, file.Id.ToString(CultureInfo.InvariantCulture))))
                        source.Files[file.Url] = File.ReadAllBytes(Path.Combine(files, file.Id.ToString(CultureInfo.InvariantCulture)));
        return source;
    }

    public Task<IReadOnlyList<ShortcutTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShortcutTeam>>(Teams);
    public Task<IReadOnlyList<ShortcutMember>> ListMembersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShortcutMember>>(Members);
    public Task<IReadOnlyList<ShortcutState>> ListStatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShortcutState>>(States);

    public Task<IReadOnlyList<ShortcutEpic>> ListEpicsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShortcutEpic>>(Epics.Values.OrderBy(epic => epic.Id).ToList());

    public Task<ShortcutEpic?> GetEpicAsync(long epicId, CancellationToken ct) => Task.FromResult(Epics.GetValueOrDefault(epicId));

    public Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(long epicId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShortcutDocument>>(Documents.GetValueOrDefault(epicId) ?? []);

    public Task<IReadOnlyList<ShortcutStorySummary>> ListStoriesAsync(DateTime? updatedSince, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShortcutStorySummary>>(Stories.Values
            .Where(story => updatedSince is null ? !story.Archived : story.UpdatedAt >= updatedSince.Value)
            .OrderBy(story => story.Id).Select(story => new ShortcutStorySummary(story.Id, story.UpdatedAt, story.Archived)).ToList());

    public Task<int> CountArchivedStoriesAsync(CancellationToken ct) => Task.FromResult(Stories.Values.Count(story => story.Archived));

    public Task<ShortcutStory?> GetStoryAsync(long storyId, CancellationToken ct)
    {
        StoryReads++;
        return Task.FromResult(Stories.GetValueOrDefault(storyId));
    }

    public Task<byte[]?> DownloadFileAsync(string url, long maxBytes, CancellationToken ct) =>
        Files.TryGetValue(url, out var bytes)
            ? Task.FromResult(bytes.LongLength <= maxBytes ? bytes : null)
            : Task.FromException<byte[]?>(new FileNotFoundException($"No recorded file for {url}."));
}
