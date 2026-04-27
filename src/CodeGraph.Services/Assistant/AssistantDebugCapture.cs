using System.Text.Json;
using System.Threading;
using CodeGraph.Data;
using CodeGraph.Models;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Assistant;

public sealed record AssistantDebugRunContext(
    long RunId,
    string ChatId,
    string Username);

public sealed record AssistantDebugExchangeCapture(
    int TurnIndex,
    string Provider,
    string Model,
    string RequestBodyJson,
    string RequestText,
    string? ResponseBodyJson = null,
    string? ResponseText = null,
    string? ToolUsesJson = null,
    string? RequestMetadataJson = null,
    string? ResponseMetadataJson = null,
    string? RequestId = null,
    string? ResponseId = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null);

public interface IAssistantDebugCapture
{
    IDisposable BeginRun(AssistantDebugRunContext context);
    Task CaptureExchangeAsync(AssistantDebugExchangeCapture exchange, CancellationToken ct = default);
}

public sealed class AssistantDebugCapture(
    IAssistantRunStore store,
    ILogger<AssistantDebugCapture> logger) : IAssistantDebugCapture
{
    private const int MaxCapturedTextLength = 128_000;
    private readonly AsyncLocal<ContextFrame?> current = new();

    public IDisposable BeginRun(AssistantDebugRunContext context)
    {
        var previous = current.Value;
        var frame = new ContextFrame(context, previous);
        current.Value = frame;
        return new Scope(this, frame);
    }

    public async Task CaptureExchangeAsync(AssistantDebugExchangeCapture exchange, CancellationToken ct = default)
    {
        var frame = current.Value;
        if (frame is null)
            return;

        try
        {
            await store.AppendAssistantDebugExchangeAsync(new AssistantDebugExchangeEntity
            {
                RunId = frame.Context.RunId,
                ChatId = frame.Context.ChatId,
                Username = frame.Context.Username,
                ExchangeIndex = Interlocked.Increment(ref frame.NextExchangeIndex),
                TurnIndex = exchange.TurnIndex,
                Provider = exchange.Provider,
                Model = exchange.Model,
                RequestId = Truncate(exchange.RequestId),
                ResponseId = Truncate(exchange.ResponseId),
                ToolUsesJson = Truncate(exchange.ToolUsesJson),
                RequestMetadataJson = Truncate(exchange.RequestMetadataJson),
                ResponseMetadataJson = Truncate(exchange.ResponseMetadataJson),
                RequestBodyJson = Truncate(exchange.RequestBodyJson) ?? "{}",
                ResponseBodyJson = Truncate(exchange.ResponseBodyJson),
                RequestText = Truncate(exchange.RequestText) ?? "",
                ResponseText = Truncate(exchange.ResponseText),
                InputTokens = exchange.InputTokens,
                OutputTokens = exchange.OutputTokens,
                TotalTokens = exchange.TotalTokens,
                CreatedAt = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to capture assistant debug exchange for run {RunId}", frame.Context.RunId);
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxCapturedTextLength)
            return value;

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            originalLength = value.Length,
            preview = value[..MaxCapturedTextLength]
        }, CodeGraphJsonDefaults.CamelCase);
    }

    private sealed class ContextFrame(AssistantDebugRunContext context, ContextFrame? previous)
    {
        public AssistantDebugRunContext Context { get; } = context;
        public ContextFrame? Previous { get; } = previous;
        public long NextExchangeIndex = -1;
    }

    private sealed class Scope(AssistantDebugCapture owner, ContextFrame frame) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            if (ReferenceEquals(owner.current.Value, frame))
                owner.current.Value = frame.Previous;

            disposed = true;
        }
    }
}
