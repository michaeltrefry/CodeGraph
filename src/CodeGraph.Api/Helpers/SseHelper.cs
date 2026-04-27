using System.Text;
using System.Text.Json;

namespace CodeGraph.Api.Helpers;

public static class SseHelper
{
    public static Task WriteHeartbeatAsync(HttpResponse response, CancellationToken ct) =>
        WriteRawAsync(response, ":\n\n", ct);

    public static async Task WriteEventAsync(
        HttpResponse response,
        string type,
        object? content,
        CancellationToken ct,
        JsonSerializerOptions? options = null,
        string? id = null)
    {
        if (!string.IsNullOrWhiteSpace(id))
            await WriteRawAsync(response, $"id: {id}\n", ct);

        var json = JsonSerializer.Serialize(new { type, content }, options);
        await WriteRawAsync(response, $"data: {json}\n\n", ct);
    }

    private static async Task WriteRawAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
