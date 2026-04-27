using System.Collections.Concurrent;
using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Configuration;

public interface IDbBackedAssistantSettingsResolver
{
    Task<LlmAssistantRuntimeConfig> GetAssistantAsync(CancellationToken ct = default);
}

public sealed class DbBackedAssistantSettingsResolver : IDbBackedAssistantSettingsResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const string CacheKey = "assistant";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AnalysisOptions> optionsAccessor;
    private readonly ILlmCatalogValidator catalogValidator;
    private readonly ConcurrentDictionary<string, CachedAssistantConfig> cache = new(StringComparer.OrdinalIgnoreCase);

    public DbBackedAssistantSettingsResolver(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisOptions> optionsAccessor,
        ILlmCatalogValidator catalogValidator,
        ILlmConfigInvalidator invalidator)
    {
        this.scopeFactory = scopeFactory;
        this.optionsAccessor = optionsAccessor;
        this.catalogValidator = catalogValidator;
        invalidator.AssistantChanged += InvalidateAssistant;
    }

    public async Task<LlmAssistantRuntimeConfig> GetAssistantAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(CacheKey, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Config;
        }

        var resolved = await ResolveUncachedAsync(ct);
        cache[CacheKey] = new CachedAssistantConfig(resolved, now.Add(CacheTtl));
        return resolved;
    }

    private async Task<LlmAssistantRuntimeConfig> ResolveUncachedAsync(CancellationToken ct)
    {
        var fallback = LlmAssistantRuntimeConfig.FromOptions(optionsAccessor.Value);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<ILlmConfigRepository>();
        if (repository is null)
        {
            return fallback;
        }

        var config = await repository.GetAssistantAsync(ct);
        if (config is null)
        {
            return fallback;
        }

        var resolved = fallback with
        {
            DefaultProvider = FirstNonEmpty(config.DefaultProvider, fallback.DefaultProvider),
            DefaultModel = FirstNonEmpty(config.DefaultModel, fallback.DefaultModel),
            MaxTokens = config.MaxTokens ?? fallback.MaxTokens,
            MaxTurns = config.MaxTurns ?? fallback.MaxTurns,
            UpdatedBy = config.UpdatedBy,
            UpdatedAtUtc = config.UpdatedAtUtc,
            HasDbConfig = true
        };

        await catalogValidator.EnsureProviderModelAsync(resolved.DefaultProvider, resolved.DefaultModel, ct);
        return resolved;
    }

    private void InvalidateAssistant()
    {
        cache.TryRemove(CacheKey, out _);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record CachedAssistantConfig(LlmAssistantRuntimeConfig Config, DateTimeOffset ExpiresAtUtc);
}

public sealed record LlmAssistantRuntimeConfig(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokens,
    int MaxTurns,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    bool HasDbConfig)
{
    public static LlmAssistantRuntimeConfig FromOptions(AnalysisOptions options)
    {
        var provider = string.IsNullOrWhiteSpace(options.Assistant.Provider)
            ? FirstNonEmpty(options.DefaultProvider, LlmProviderKeys.Anthropic)
            : options.Assistant.Provider.Trim();
        var normalizedProvider = LlmProviderKeys.IsKnown(provider)
            ? LlmProviderKeys.Normalize(provider)
            : provider;

        return new LlmAssistantRuntimeConfig(
            normalizedProvider,
            ResolveModel(options, normalizedProvider),
            options.Assistant.MaxTokens,
            options.Assistant.MaxTurns,
            UpdatedBy: null,
            UpdatedAtUtc: null,
            HasDbConfig: false);
    }

    private static string ResolveModel(AnalysisOptions options, string provider) =>
        FirstNonEmpty(
            options.Assistant.Model,
            provider switch
            {
                LlmProviderKeys.OpenAi => options.OpenAi.Model,
                LlmProviderKeys.LmStudio => options.LmStudio.Model,
                _ => null
            },
            options.Model);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
