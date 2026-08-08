using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CodeGraph.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeGraph.Host.Shared.Logging;

public sealed class ApplicationDatabaseLoggingOptions
{
    public string ServiceName { get; set; } = ApplicationLogServices.Api;
    public bool Enabled { get; set; } = true;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public int Capacity { get; set; } = 5_000;
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalMilliseconds { get; set; } = 1_000;
    public int RetentionDays { get; set; } = 30;
}

internal sealed class ApplicationLogChannel(IOptions<ApplicationDatabaseLoggingOptions> configuredOptions)
{
    private readonly ApplicationDatabaseLoggingOptions _options = configuredOptions.Value;

    public Channel<ApplicationLogEntryEntity> Entries { get; } = Channel.CreateBounded<ApplicationLogEntryEntity>(
        new BoundedChannelOptions(Math.Clamp(configuredOptions.Value.Capacity, 100, 100_000))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public bool TryWrite(ApplicationLogEntryEntity entry) =>
        _options.Enabled && Entries.Writer.TryWrite(entry);
}

internal sealed class ApplicationDatabaseLoggerProvider(
    ApplicationLogChannel channel,
    IOptions<ApplicationDatabaseLoggingOptions> configuredOptions) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, ApplicationDatabaseLogger> _loggers = new();
    private readonly ApplicationDatabaseLoggingOptions _options = configuredOptions.Value;
    private readonly string _service = Truncate(configuredOptions.Value.ServiceName, 32);
    private readonly string _source = Truncate(
        $"{AppDomain.CurrentDomain.FriendlyName}@{Environment.MachineName}",
        128);
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category =>
            new ApplicationDatabaseLogger(category, _service, _source, channel, _options, () => _scopeProvider));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose() => _loggers.Clear();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

internal sealed class ApplicationDatabaseLogger(
    string category,
    string service,
    string source,
    ApplicationLogChannel channel,
    ApplicationDatabaseLoggingOptions options,
    Func<IExternalScopeProvider> scopeProviderAccessor) : ILogger
{
    private const int MaxMessageLength = 16_384;
    private const int MaxExceptionLength = 65_536;
    private const int MaxPropertyValueLength = 2_048;
    private const int MaxProperties = 64;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        scopeProviderAccessor().Push(state);

    public bool IsEnabled(LogLevel logLevel) =>
        options.Enabled
        && logLevel != LogLevel.None
        && logLevel >= options.MinimumLevel
        && !category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        && !category.StartsWith("CodeGraph.Host.Shared.Logging", StringComparison.Ordinal);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        try
        {
            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
                return;

            var activity = Activity.Current;
            channel.TryWrite(new ApplicationLogEntryEntity
            {
                OccurredAtUtc = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Service = service,
                Source = source,
                Category = Truncate(category, 512),
                EventId = eventId.Id,
                Message = Truncate(message ?? string.Empty, MaxMessageLength),
                Exception = exception is null ? null : Truncate(exception.ToString(), MaxExceptionLength),
                TraceId = activity?.TraceId.ToHexString(),
                SpanId = activity?.SpanId.ToHexString(),
                PropertiesJson = SerializeProperties(state)
            });
        }
        catch
        {
            // Logging must never affect the application code path.
        }
    }

    private string? SerializeProperties<TState>(TState state)
    {
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        AppendProperties(state, properties);
        scopeProviderAccessor().ForEachScope((scope, target) => AppendProperties(scope, target), properties);
        return properties.Count == 0 ? null : JsonSerializer.Serialize(properties);
    }

    private static void AppendProperties(object? state, Dictionary<string, string?> target)
    {
        if (target.Count >= MaxProperties || state is null)
            return;

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (target.Count >= MaxProperties || pair.Key == "{OriginalFormat}")
                    break;

                target[pair.Key] = pair.Value is null
                    ? null
                    : Truncate(pair.Value.ToString() ?? string.Empty, MaxPropertyValueLength);
            }
            return;
        }

        target.TryAdd("Scope", Truncate(state.ToString() ?? string.Empty, MaxPropertyValueLength));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

internal sealed class ApplicationDatabaseLogWriter(
    ApplicationLogChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<ApplicationDatabaseLoggingOptions> configuredOptions) : BackgroundService
{
    private readonly ApplicationDatabaseLoggingOptions _options = configuredOptions.Value;
    private DateTime _nextRetentionUtc = DateTime.MinValue;
    private DateTime _nextFailureReportUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var batchSize = Math.Clamp(_options.BatchSize, 1, 1_000);
        var flushDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(_options.FlushIntervalMilliseconds, 100, 30_000));
        var batch = new List<ApplicationLogEntryEntity>(batchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                batch.Clear();
                while (batch.Count < batchSize && channel.Entries.Reader.TryRead(out var entry))
                    batch.Add(entry);

                await PersistAsync(batch, stoppingToken);
                if (batch.Count < batchSize)
                    await Task.Delay(flushDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                while (!flushTimeout.IsCancellationRequested)
                {
                    batch.Clear();
                    while (batch.Count < batchSize && channel.Entries.Reader.TryRead(out var entry))
                        batch.Add(entry);
                    if (batch.Count == 0)
                        break;

                    await PersistAsync(batch, flushTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (flushTimeout.IsCancellationRequested)
            {
                // Best-effort shutdown flush reached its bound.
            }
        }
    }

    private async Task PersistAsync(
        IReadOnlyList<ApplicationLogEntryEntity> batch,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var retentionDue = _options.RetentionDays > 0 && now >= _nextRetentionUtc;
        if (batch.Count == 0 && !retentionDue)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IApplicationLogStore>();
            if (batch.Count > 0)
                await store.WriteBatchAsync(batch, cancellationToken);

            if (retentionDue)
            {
                await store.DeleteBeforeAsync(now.AddDays(-_options.RetentionDays), cancellationToken);
                _nextRetentionUtc = now.AddDays(1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureNow = DateTime.UtcNow;
            if (failureNow >= _nextFailureReportUtc)
            {
                Console.Error.WriteLine($"Application database logging failed: {ex.Message}");
                _nextFailureReportUtc = failureNow.AddMinutes(5);
            }
        }
    }
}

public static class ApplicationDatabaseLoggingServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationDatabaseLogging(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!ApplicationLogServices.IsSupported(serviceName))
            throw new ArgumentOutOfRangeException(nameof(serviceName), serviceName, "Unsupported application log service.");

        services.Configure<ApplicationDatabaseLoggingOptions>(
            configuration.GetSection("Logging:Database"));
        services.PostConfigure<ApplicationDatabaseLoggingOptions>(options =>
            options.ServiceName = ApplicationLogServices.Normalize(serviceName)!);
        services.AddSingleton<ApplicationLogChannel>();
        services.AddSingleton<ILoggerProvider, ApplicationDatabaseLoggerProvider>();
        services.AddHostedService<ApplicationDatabaseLogWriter>();
        return services;
    }
}
