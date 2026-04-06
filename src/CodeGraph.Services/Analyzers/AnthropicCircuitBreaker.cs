using System.Net;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Circuit breaker for Anthropic API HTTP calls.
/// Retries transient failures (429, 5xx, timeouts) with exponential backoff.
/// Opens the circuit after consecutive failures to avoid hammering a down API.
/// </summary>
public class AnthropicCircuitBreaker(ILogger<AnthropicCircuitBreaker> logger)
{
    private const int MaxRetries = 3;
    private const int FailureThreshold = 5;
    private static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(2);

    private int _consecutiveFailures;
    private long _circuitOpenedAtTicks = DateTime.MinValue.Ticks;

    public bool IsOpen =>
        Volatile.Read(ref _consecutiveFailures) >= FailureThreshold &&
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _circuitOpenedAtTicks), DateTimeKind.Utc) < CooldownPeriod;

    /// <summary>
    /// Execute an HTTP request with retry and circuit breaker protection.
    /// Returns the successful response or throws after exhausting retries.
    /// </summary>
    public async Task<HttpResponseMessage> ExecuteAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct = default)
    {
        if (IsOpen)
        {
            var remaining = CooldownPeriod - (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _circuitOpenedAtTicks), DateTimeKind.Utc));
            throw new InvalidOperationException(
                $"Anthropic API circuit breaker is open. Retry after {remaining.TotalSeconds:F0}s.");
        }

        var delay = TimeSpan.FromSeconds(5);

        for (int attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return response;
                }

                if (IsTransient(response.StatusCode) && attempt < MaxRetries)
                {
                    var retryAfter = GetRetryAfter(response) ?? delay;
                    logger.LogWarning(
                        "Anthropic API returned {Status}, retrying in {Delay}s ({Attempt}/{Max})",
                        (int)response.StatusCode, retryAfter.TotalSeconds, attempt + 1, MaxRetries);
                    response.Dispose();
                    await Task.Delay(retryAfter, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                    continue;
                }

                // Non-transient or retries exhausted
                RecordFailure();
                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex,
                    "Anthropic API request failed, retrying in {Delay}s ({Attempt}/{Max})",
                    delay.TotalSeconds, attempt + 1, MaxRetries);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                logger.LogWarning(ex,
                    "Anthropic API request timed out, retrying in {Delay}s ({Attempt}/{Max})",
                    delay.TotalSeconds, attempt + 1, MaxRetries);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private void RecordFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= FailureThreshold)
        {
            Interlocked.Exchange(ref _circuitOpenedAtTicks, DateTime.UtcNow.Ticks);
            logger.LogError(
                "Anthropic API circuit breaker opened after {Count} consecutive failures. " +
                "Will retry after {Cooldown}s.",
                failures, CooldownPeriod.TotalSeconds);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }
}
