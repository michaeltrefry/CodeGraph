namespace CodeGraph.Services.Configuration;

internal static class AnthropicEndpointUrls
{
    // EndpointUrl is an API base (or an explicit messages/batches endpoint). Only a bare
    // origin needs the default /v1; configured proxy prefixes and versions remain intact.
    public static string Messages(string? endpointUrl, string messagesApiUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            return string.IsNullOrWhiteSpace(messagesApiUrl) ? "https://api.anthropic.com/v1/messages" : messagesApiUrl;
        var uri = new UriBuilder(endpointUrl);
        var path = uri.Path.TrimEnd('/');
        if (path.EndsWith("/messages/batches", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/batches".Length];
        else if (!path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            path = (path.Length == 0 ? "/v1" : path) + "/messages";
        uri.Path = path;
        return uri.Uri.AbsoluteUri;
    }

    public static string Batches(string? endpointUrl, string batchApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            var fallback = new UriBuilder(string.IsNullOrWhiteSpace(batchApiBaseUrl) ? "https://api.anthropic.com/v1/messages/batches" : batchApiBaseUrl);
            fallback.Path = fallback.Path.TrimEnd('/');
            return fallback.Uri.AbsoluteUri;
        }
        var uri = new UriBuilder(Messages(endpointUrl, batchApiBaseUrl));
        uri.Path = uri.Path.TrimEnd('/') + "/batches";
        return uri.Uri.AbsoluteUri;
    }

    public static string BatchResource(string? endpointUrl, string batchApiBaseUrl, string batchId, bool results = false)
    {
        var uri = new UriBuilder(Batches(endpointUrl, batchApiBaseUrl));
        uri.Path = uri.Path.TrimEnd('/') + "/" + Uri.EscapeDataString(batchId) + (results ? "/results" : "");
        return uri.Uri.AbsoluteUri;
    }
}
