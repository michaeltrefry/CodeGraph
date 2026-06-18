using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Query;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Mcp.Hub;

public sealed class McpHubService(
    IMcpHubStore store,
    IProjectQueryService projectQuery,
    IHttpClientFactory httpClientFactory,
    SensitiveColumnPolicy sensitiveColumnPolicy,
    MySqlSourceExposurePolicy sourceExposurePolicy,
    ILogger<McpHubService> logger)
{
    public const string ShortcutCredentialKey = "apiToken";
    public const string LegacyShortcutShimProviderKey = "shortcut-shim";
    public const string LegacyShortcutShimCredentialKey = McpShimDiscoveryService.DiscoveryTokenCredentialKey;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<McpHubCatalogResponse> GetCatalogAsync(CancellationToken ct = default)
    {
        var providers = await store.ListProvidersAsync(ct);
        var tools = await store.ListToolsAsync(ct);
        return new McpHubCatalogResponse(
            providers.Select(ToResponse).ToList(),
            tools.Select(ToResponse).ToList());
    }

    public async Task<string> SearchShortcutEpicsAsync(string? query, string? username, CancellationToken ct = default)
    {
        return await InvokeShortcutApiAsync(
            username,
            HttpMethod.Get,
            "search/epics",
            bodyJson: null,
            query: BuildQuery(("query", query)),
            ct);
    }

    public async Task<string> SearchShortcutStoriesAsync(string? query, string? username, CancellationToken ct = default)
    {
        return await InvokeShortcutApiAsync(
            username,
            HttpMethod.Get,
            "search/stories",
            bodyJson: null,
            query: BuildQuery(("query", query)),
            ct);
    }

    public async Task<string> InvokeShortcutApiAsync(
        string? username,
        HttpMethod method,
        string path,
        string? bodyJson = null,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("shortcut", ct);
        var client = await CreateShortcutClientAsync(username, ct);
        var requestUri = BuildProviderUri(path, query);
        using var request = new HttpRequestMessage(method, requestUri);
        if (bodyJson is not null)
        {
            ValidateJsonObject(bodyJson);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct);
        return await ReadProviderResponseAsync(response, ct);
    }

    public async Task<string> UploadShortcutFileAsync(
        string? username,
        int storyPublicId,
        string filePath,
        CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("shortcut", ct);
        var client = await CreateShortcutClientAsync(username, ct);
        if (storyPublicId <= 0)
            throw new McpHubProviderPolicyException("storyPublicId must be positive.");
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new McpHubProviderPolicyException("filePath must point to an existing local file.");

        await using var stream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(storyPublicId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "story_id");
        content.Add(new StreamContent(stream), "file0", Path.GetFileName(filePath));

        using var response = await client.PostAsync("files", content, ct);
        return await ReadProviderResponseAsync(response, ct);
    }

    public async Task<string> ListRabbitMqQueuesAsync(string? virtualHost, CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("rabbitmq", ct);
        if (string.IsNullOrWhiteSpace(virtualHost))
            throw new McpHubProviderPolicyException("RabbitMQ virtualHost is required; all-vhost listing is not allowed.");

        var vhostName = virtualHost.Trim();
        await EnsureRabbitResourceAllowedAsync(vhostName, "*", ct);
        var client = await CreateRabbitMqClientAsync(ct);
        var vhost = Uri.EscapeDataString(vhostName);
        var path = $"api/queues/{vhost}";
        using var response = await client.GetAsync(path, ct);
        return await ReadProviderResponseAsync(response, ct);
    }

    public async Task<string> GetRabbitMqQueueAsync(string virtualHost, string queue, CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("rabbitmq", ct);
        if (string.IsNullOrWhiteSpace(virtualHost) || string.IsNullOrWhiteSpace(queue))
            return "virtualHost and queue are required.";

        await EnsureRabbitResourceAllowedAsync(virtualHost.Trim(), queue.Trim(), ct);
        var client = await CreateRabbitMqClientAsync(ct);
        using var response = await client.GetAsync(
            $"api/queues/{Uri.EscapeDataString(virtualHost.Trim())}/{Uri.EscapeDataString(queue.Trim())}",
            ct);
        return await ReadProviderResponseAsync(response, ct);
    }

    private const int MaxPeekMessages = 20;
    private const int MaxPeekPayloadBytes = 8 * 1024;

    /// <summary>
    /// Non-destructively peeks messages from a queue. Uses the RabbitMQ management
    /// <c>ackmode = ack_requeue_true</c> so the messages are returned to the queue, and caps
    /// both the message count and the per-message payload size — see Shortcut sc-1057.
    /// </summary>
    public async Task<string> PeekRabbitMqQueueAsync(
        string virtualHost,
        string queue,
        int? count,
        CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("rabbitmq", ct);
        if (string.IsNullOrWhiteSpace(virtualHost) || string.IsNullOrWhiteSpace(queue))
            return "virtualHost and queue are required.";

        await EnsureRabbitResourceAllowedAsync(virtualHost.Trim(), queue.Trim(), ct);
        var client = await CreateRabbitMqClientAsync(ct);
        var body = new
        {
            count = Math.Clamp(count ?? 5, 1, MaxPeekMessages),
            ackmode = "ack_requeue_true",
            encoding = "auto",
            truncate = MaxPeekPayloadBytes,
        };

        using var response = await client.PostAsJsonAsync(
            $"api/queues/{Uri.EscapeDataString(virtualHost.Trim())}/{Uri.EscapeDataString(queue.Trim())}/get",
            body,
            JsonOptions,
            ct);
        return await ReadProviderResponseAsync(response, ct);
    }

    public async Task<string> ListSchemasAsync(
        string? search,
        string? server,
        string? database,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("mysql", ct);
        var result = await projectQuery.ListSchemasAsync(
            search,
            server,
            database,
            Math.Max(1, page),
            Math.Clamp(pageSize, 1, 100));
        if (await IsSourceVisibleAsync("mysql", ct))
            return JsonSerializer.Serialize(result, JsonOptions);

        var redacted = result with
        {
            Items = result.Items
                .Select(item => item with
                {
                    ServerName = "redacted",
                    DatabaseName = "redacted"
                })
                .ToList(),
            Servers = [],
            Databases = []
        };
        return JsonSerializer.Serialize(redacted, JsonOptions);
    }

    public async Task<string> GetSchemaCatalogAsync(string name, CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("mysql", ct);
        if (string.IsNullOrWhiteSpace(name))
            return "name is required.";

        var result = await projectQuery.GetSchemaCatalogAsync(name.Trim());
        if (result is null)
            return $"Schema project '{name}' was not found. Call mysql_list_schemas first.";

        if (!await IsSourceVisibleAsync("mysql", ct))
        {
            result = result with
            {
                ServerName = "redacted",
                DatabaseName = "redacted"
            };
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public async Task<string> RunReadOnlySqlAsync(string source, string sql, int? limit, CancellationToken ct = default)
    {
        await EnsureProviderEnabledAsync("mysql", ct);

        // The source must be explicitly opted into the hub AND set to the ReadOnlySql exposure
        // mode; resolution and gating happen before anything else (see sc-1058).
        var exposedSource = await sourceExposurePolicy.ResolveReadOnlySqlSourceAsync(source, ct);

        var validation = ReadOnlySqlValidator.Validate(sql);
        if (!validation.IsValid)
            throw new McpHubProviderPolicyException(validation.ErrorMessage!);

        // Sensitive-column enforcement happens here — before any database round-trip — using
        // the parsed table/column set rather than result-reader column names (see sc-1051).
        await sensitiveColumnPolicy.EnsureQueryAllowedAsync(exposedSource.Id.ToString(), validation, ct);

        var connectionString = exposedSource.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return "The exposed MySQL source has no connection string configured.";

        var boundedSql = ApplyLimit(sql.Trim().TrimEnd(';'), Math.Clamp(limit ?? 100, 1, 500));
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(boundedSql, connection)
        {
            CommandTimeout = 30
        };
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct) && rows.Count < Math.Clamp(limit ?? 100, 1, 500))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return JsonSerializer.Serialize(new { rows, rowCount = rows.Count }, JsonOptions);
    }

    public async Task AuditAsync(
        string? username,
        long? tokenId,
        string providerKey,
        string toolName,
        string action,
        string operation,
        string? resourceKey,
        string credentialMode,
        string authorizationDecision,
        string statusClass,
        int durationMs,
        bool success,
        string? message,
        CancellationToken ct = default,
        string providerType = "provider")
    {
        try
        {
            await store.CreateAuditAsync(new McpHubAuditEntity
            {
                Username = Normalize(username),
                TokenId = tokenId,
                ProviderKey = providerKey,
                ProviderType = NormalizeAuditValue(providerType, "provider"),
                ToolName = toolName,
                Action = action,
                Operation = NormalizeAuditValue(operation, "invoke"),
                ResourceKey = Normalize(resourceKey),
                CredentialMode = NormalizeAuditValue(credentialMode, "none"),
                AuthorizationDecision = NormalizeAuditValue(authorizationDecision, "unknown"),
                StatusClass = NormalizeAuditValue(statusClass, "unknown"),
                DurationMs = Math.Max(0, durationMs),
                Success = success,
                Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                CreatedAtUtc = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to write MCP hub audit for {ToolName}", toolName);
        }
    }

    // Shortcut uses the hub-shared API token. The retired shim stored the same token as
    // shortcut-shim/discoveryToken, so keep that as a compatibility fallback.
    private async Task<HttpClient> CreateShortcutClientAsync(string? username, CancellationToken ct)
    {
        var token = await store.GetCredentialValueAsync("shortcut", ShortcutCredentialKey, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = await store.GetCredentialValueAsync(
                LegacyShortcutShimProviderKey,
                LegacyShortcutShimCredentialKey,
                ct);
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new McpHubProviderPolicyException(
                "No shared Shortcut API token is configured. Configure shortcut/apiToken or legacy shortcut-shim/discoveryToken in MCP Hub credentials.");

        return BuildShortcutClient(token);
    }

    private HttpClient BuildShortcutClient(string token)
    {
        var client = httpClientFactory.CreateClient("mcp-hub-shortcut");
        client.DefaultRequestHeaders.Remove("Shortcut-Token");
        client.DefaultRequestHeaders.Add("Shortcut-Token", token.Trim());
        return client;
    }

    /// <summary>
    /// Validates a Shortcut API token by calling the Shortcut <c>member</c> endpoint.
    /// Proves the token works and captures provider-side identity for display/audit. The
    /// provider identity is NOT required to match the CodeGraph username.
    /// </summary>
    public async Task<DelegatedCredentialValidationResult> ValidateShortcutCredentialAsync(
        string? token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new DelegatedCredentialValidationResult(false, null, "A Shortcut API token is required.");

        try
        {
            var client = BuildShortcutClient(token);
            using var response = await client.GetAsync("member", ct);
            if (!response.IsSuccessStatusCode)
                return new DelegatedCredentialValidationResult(
                    false,
                    null,
                    $"Shortcut rejected the token ({(int)response.StatusCode} {response.ReasonPhrase}).");

            var body = await response.Content.ReadAsStringAsync(ct);
            return new DelegatedCredentialValidationResult(true, ExtractShortcutIdentity(body), "Token validated.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new DelegatedCredentialValidationResult(false, null, $"Could not reach Shortcut: {ex.Message}");
        }
    }

    private static string? ExtractShortcutIdentity(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? name = root.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            string? mention = root.TryGetProperty("mention_name", out var mentionElement)
                && mentionElement.ValueKind == JsonValueKind.String
                ? mentionElement.GetString()
                : null;

            return (name, mention) switch
            {
                ({ } n, { } m) => $"{n} (@{m})",
                ({ } n, null) => n,
                (null, { } m) => "@" + m,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> IsSourceVisibleAsync(string providerKey, CancellationToken ct)
    {
        var providers = await store.ListProvidersAsync(ct);
        return providers.FirstOrDefault(provider => string.Equals(provider.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            ?.SourceVisible == true;
    }

    private async Task<HttpClient> CreateRabbitMqClientAsync(CancellationToken ct)
    {
        var baseUrl = await store.GetConfigValueAsync("rabbitmq", "managementBaseUrl", ct);
        var username = await store.GetCredentialValueAsync("rabbitmq", "username", ct);
        var password = await store.GetCredentialValueAsync("rabbitmq", "password", ct);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || password is null)
            throw new InvalidOperationException("RabbitMQ managementBaseUrl, username, and password are required.");

        var client = httpClientFactory.CreateClient("mcp-hub-rabbitmq");
        client.BaseAddress = new Uri(baseUrl.Trim().TrimEnd('/') + "/");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new("Basic", basic);
        return client;
    }

    private static async Task<string> ReadProviderResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxBytes = 64 * 1024;
        var body = await ReadCappedBodyAsync(response, maxBytes, ct);
        if (response.IsSuccessStatusCode)
            return string.IsNullOrWhiteSpace(body) ? "{}" : body;

        return JsonSerializer.Serialize(new
        {
            status = (int)response.StatusCode,
            reason = response.ReasonPhrase,
            body
        }, JsonOptions);
    }

    private async Task EnsureProviderEnabledAsync(string providerKey, CancellationToken ct)
    {
        var providers = await store.ListProvidersAsync(ct);
        var provider = providers.FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new McpHubProviderPolicyException($"Provider '{providerKey}' is not cataloged.");
        if (!provider.Enabled)
            throw new McpHubProviderPolicyException($"Provider '{providerKey}' is disabled.");
    }

    private async Task EnsureRabbitResourceAllowedAsync(string virtualHost, string queue, CancellationToken ct)
    {
        var policy = await store.GetConfigValueAsync("rabbitmq", "allowedQueues", ct);
        if (!MatchesResourcePolicy(policy, virtualHost, queue))
            throw new McpHubProviderPolicyException($"RabbitMQ resource '{virtualHost}/{queue}' is not allowed by policy.");
    }

    private static bool MatchesResourcePolicy(string? policy, string virtualHost, string queue)
    {
        var resource = $"{virtualHost}/{queue}";
        return SplitPolicy(policy).Any(entry =>
            string.Equals(entry, "*/*", StringComparison.Ordinal) ||
            string.Equals(entry, resource, StringComparison.Ordinal) ||
            entry.EndsWith("/*", StringComparison.Ordinal) &&
            string.Equals(entry[..^2], virtualHost, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> SplitPolicy(string? policy) =>
        string.IsNullOrWhiteSpace(policy)
            ? []
            : policy
                .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static async Task<string> ReadCappedBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        var remaining = maxBytes + 1;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining)), ct);
            if (read == 0)
                break;
            await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
            remaining -= read;
        }

        var body = Encoding.UTF8.GetString(buffer.ToArray());
        if (buffer.Length <= maxBytes)
            return body;

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            maxBytes,
            body = body[..Math.Min(body.Length, maxBytes)]
        }, JsonOptions);
    }

    private static string ApplyLimit(string sql, int limit)
    {
        if (!Regex.IsMatch(sql, @"^\s*select\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(sql, @"\blimit\s+\d+\b", RegexOptions.IgnoreCase))
        {
            return sql;
        }

        return $"{sql} LIMIT {limit}";
    }

    public static IReadOnlyDictionary<string, string?> BuildQuery(params (string Name, object? Value)[] values)
    {
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name) || value is null)
                continue;

            var stringValue = value switch
            {
                string text when string.IsNullOrWhiteSpace(text) => null,
                string text => text.Trim(),
                bool boolean => boolean ? "true" : "false",
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString()
            };

            if (!string.IsNullOrWhiteSpace(stringValue))
                query[name] = stringValue;
        }

        return query;
    }

    public static string JsonBody(params (string Name, object? Value)[] values)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
                body[name] = value;
        }

        return JsonSerializer.Serialize(body, JsonOptions);
    }

    private static string BuildProviderUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpHubProviderPolicyException("Shortcut API path is required.");

        var trimmedPath = path.Trim().TrimStart('/');
        if (trimmedPath.StartsWith("api/v3/", StringComparison.OrdinalIgnoreCase))
            trimmedPath = trimmedPath["api/v3/".Length..];

        if (query is null || query.Count == 0)
            return trimmedPath;

        var pairs = query
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!.Trim())}")
            .ToArray();
        return pairs.Length == 0 ? trimmedPath : $"{trimmedPath}?{string.Join("&", pairs)}";
    }

    private static void ValidateJsonObject(string bodyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new McpHubProviderPolicyException("Shortcut request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new McpHubProviderPolicyException($"Shortcut request body is not valid JSON: {ex.Message}");
        }
    }

    private static McpHubProviderResponse ToResponse(McpHubProviderEntity entity) =>
        new(
            entity.ProviderKey,
            entity.DisplayName,
            entity.Description,
            entity.Enabled,
            entity.SourceVisible,
            entity.UpdatedAtUtc);

    private static McpHubToolResponse ToResponse(McpHubToolEntity entity) =>
        new(
            entity.ToolName,
            entity.ProviderKey,
            entity.ProviderType,
            entity.DisplayName,
            entity.Description,
            entity.ReadOnly,
            entity.Destructive,
            entity.Enabled,
            entity.IsAvailable,
            entity.DefaultSelected,
            entity.AccessClass,
            entity.RequiresCredential,
            entity.UpdatedAtUtc);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string NormalizeAuditValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
}

public sealed class McpHubProviderPolicyException(string message) : InvalidOperationException(message);

public sealed record DelegatedCredentialValidationResult(
    bool IsValid,
    string? ProviderIdentity,
    string? Message);
