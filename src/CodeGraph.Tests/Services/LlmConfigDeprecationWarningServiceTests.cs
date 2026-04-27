using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class LlmConfigDeprecationWarningServiceTests
{
    [Fact]
    public async Task LogWarningsAsync_WarnsForCustomizedAppsettingsWithoutDatabaseRows()
    {
        var repository = new RecordingLlmConfigRepository();
        var logger = new RecordingLogger<LlmConfigDeprecationWarningService>();
        var service = CreateService(
            repository,
            new AnalysisOptions
            {
                Anthropic = new AnthropicAnalysisProviderOptions
                {
                    ApiKey = "sk-ant",
                    Version = "2024-01-01"
                },
                MaxParallelAnalyses = 9,
                Review = new ReviewOptions
                {
                    Model = "claude-review"
                }
            },
            logger);

        await service.LogWarningsAsync();

        logger.WarningSettingKeys.ShouldBe([
            "provider.anthropic.token_encrypted",
            "provider.anthropic.api_version",
            "analysis.max_parallel_analyses",
            "review.default_model"
        ], ignoreOrder: true);
        logger.Messages.All(message => message.Contains("llm.config.deprecation", StringComparison.Ordinal)).ShouldBeTrue();
        logger.WarningSettingKeys.ShouldNotContain("provider.lmstudio.token_encrypted");
    }

    [Fact]
    public async Task LogWarningsAsync_DoesNotWarnWhenCustomizedValuesHaveDatabaseRows()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                [LlmProviderKeys.Anthropic] = new LlmProviderConfig(
                    LlmProviderKeys.Anthropic,
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: "2024-01-01",
                    Models: [],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            },
            Analysis = new LlmAnalysisConfig(
                DefaultProvider: null,
                DefaultModel: null,
                MaxTokensPerAnalysis: null,
                MaxTokensPerSynthesis: null,
                MaxFileSizeKb: null,
                MaxParallelAnalyses: 9,
                MaxSourceChars: null,
                UpdatedBy: null,
                UpdatedAtUtc: null),
            Review = new LlmReviewConfig(
                DefaultProvider: null,
                DefaultModel: "claude-review",
                MaxFilesToInspect: null,
                MaxSourceCharsPerFile: null,
                MaxInspectionPasses: null,
                MaxFindings: null,
                UpdatedBy: null,
                UpdatedAtUtc: null)
        };
        var logger = new RecordingLogger<LlmConfigDeprecationWarningService>();
        var service = CreateService(
            repository,
            new AnalysisOptions
            {
                Anthropic = new AnthropicAnalysisProviderOptions
                {
                    ApiKey = "sk-ant",
                    Version = "2024-01-01"
                },
                MaxParallelAnalyses = 9,
                Review = new ReviewOptions
                {
                    Model = "claude-review"
                }
            },
            logger);

        await service.LogWarningsAsync();

        logger.WarningSettingKeys.ShouldBeEmpty();
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersLlmConfigDeprecationWarningService()
    {
        var services = new ServiceCollection();
        services.AddCodeGraphOptions(new ConfigurationBuilder().Build());

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmConfigDeprecationWarningService));
        descriptor.ImplementationType.ShouldBe(typeof(LlmConfigDeprecationWarningService));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    private static LlmConfigDeprecationWarningService CreateService(
        RecordingLlmConfigRepository repository,
        AnalysisOptions options,
        RecordingLogger<LlmConfigDeprecationWarningService> logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConfigRepository>(repository);
        var provider = services.BuildServiceProvider();

        return new LlmConfigDeprecationWarningService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger);
    }

    private sealed class RecordingLlmConfigRepository : ILlmConfigRepository
    {
        public Dictionary<string, LlmProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public LlmAnalysisConfig? Analysis { get; set; }
        public LlmReviewConfig? Review { get; set; }
        public LlmAssistantConfig? Assistant { get; set; }

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(Providers.GetValueOrDefault(providerKey));

        public Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default) =>
            Task.FromResult(Analysis);

        public Task SetAnalysisAsync(LlmAnalysisWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmReviewConfig?> GetReviewAsync(CancellationToken ct = default) =>
            Task.FromResult(Review);

        public Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default) =>
            Task.FromResult(Assistant);

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<string> WarningSettingKeys { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }

            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var settingKey = values.FirstOrDefault(value => value.Key == "SettingKey").Value?.ToString();
                if (!string.IsNullOrWhiteSpace(settingKey))
                {
                    WarningSettingKeys.Add(settingKey);
                }
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
