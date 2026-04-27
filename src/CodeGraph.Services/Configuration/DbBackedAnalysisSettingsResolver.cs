using System.Collections.Concurrent;
using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Configuration;

public interface IDbBackedAnalysisSettingsResolver
{
    Task<LlmAnalysisRuntimeConfig> GetAnalysisAsync(CancellationToken ct = default);
}

public sealed class DbBackedAnalysisSettingsResolver : IDbBackedAnalysisSettingsResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const string CacheKey = "analysis";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AnalysisOptions> optionsAccessor;
    private readonly ILlmCatalogValidator catalogValidator;
    private readonly ConcurrentDictionary<string, CachedAnalysisConfig> cache = new(StringComparer.OrdinalIgnoreCase);

    public DbBackedAnalysisSettingsResolver(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisOptions> optionsAccessor,
        ILlmCatalogValidator catalogValidator,
        ILlmConfigInvalidator invalidator)
    {
        this.scopeFactory = scopeFactory;
        this.optionsAccessor = optionsAccessor;
        this.catalogValidator = catalogValidator;
        invalidator.AnalysisChanged += InvalidateAnalysis;
    }

    public async Task<LlmAnalysisRuntimeConfig> GetAnalysisAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(CacheKey, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Config;
        }

        var resolved = await ResolveUncachedAsync(ct);
        cache[CacheKey] = new CachedAnalysisConfig(resolved, now.Add(CacheTtl));
        return resolved;
    }

    private async Task<LlmAnalysisRuntimeConfig> ResolveUncachedAsync(CancellationToken ct)
    {
        var fallback = LlmAnalysisRuntimeConfig.FromOptions(optionsAccessor.Value);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<ILlmConfigRepository>();
        if (repository is null)
        {
            return fallback;
        }

        var config = await repository.GetAnalysisAsync(ct);
        if (config is null)
        {
            return fallback;
        }

        var resolved = fallback with
        {
            DefaultProvider = FirstNonEmpty(config.DefaultProvider, fallback.DefaultProvider),
            DefaultModel = FirstNonEmpty(config.DefaultModel, fallback.DefaultModel),
            MaxTokensPerAnalysis = config.MaxTokensPerAnalysis ?? fallback.MaxTokensPerAnalysis,
            MaxTokensPerSynthesis = config.MaxTokensPerSynthesis ?? fallback.MaxTokensPerSynthesis,
            MaxFileSizeKb = config.MaxFileSizeKb ?? fallback.MaxFileSizeKb,
            MaxParallelAnalyses = config.MaxParallelAnalyses ?? fallback.MaxParallelAnalyses,
            MaxSourceChars = config.MaxSourceChars ?? fallback.MaxSourceChars,
            UpdatedBy = config.UpdatedBy,
            UpdatedAtUtc = config.UpdatedAtUtc,
            HasDbConfig = true
        };

        await catalogValidator.EnsureProviderModelAsync(resolved.DefaultProvider, resolved.DefaultModel, ct);
        return resolved;
    }

    private void InvalidateAnalysis()
    {
        cache.TryRemove(CacheKey, out _);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record CachedAnalysisConfig(LlmAnalysisRuntimeConfig Config, DateTimeOffset ExpiresAtUtc);
}

public sealed record LlmAnalysisRuntimeConfig(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokensPerAnalysis,
    int MaxTokensPerSynthesis,
    int MaxFileSizeKb,
    int MaxParallelAnalyses,
    int MaxSourceChars,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    bool HasDbConfig)
{
    public static LlmAnalysisRuntimeConfig FromOptions(AnalysisOptions options) =>
        new(
            string.IsNullOrWhiteSpace(options.DefaultProvider) ? LlmProviderKeys.Anthropic : options.DefaultProvider.Trim(),
            string.IsNullOrWhiteSpace(options.Model) ? "" : options.Model.Trim(),
            options.MaxTokensPerAnalysis,
            options.MaxTokensPerSynthesis,
            options.MaxFileSizeKb,
            options.MaxParallelAnalyses,
            options.MaxSourceChars,
            UpdatedBy: null,
            UpdatedAtUtc: null,
            HasDbConfig: false);
}
