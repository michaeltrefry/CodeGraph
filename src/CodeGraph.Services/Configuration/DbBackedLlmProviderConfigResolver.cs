using System.Collections.Concurrent;
using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Configuration;

public interface IDbBackedLlmProviderConfigResolver
{
    Task<LlmProviderRuntimeConfig> GetProviderAsync(string providerKey, CancellationToken ct = default);
}

public sealed class DbBackedLlmProviderConfigResolver : IDbBackedLlmProviderConfigResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AnalysisOptions> optionsAccessor;
    private readonly ConcurrentDictionary<string, CachedProviderConfig> cache = new(StringComparer.OrdinalIgnoreCase);

    public DbBackedLlmProviderConfigResolver(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisOptions> optionsAccessor,
        ILlmConfigInvalidator invalidator)
    {
        this.scopeFactory = scopeFactory;
        this.optionsAccessor = optionsAccessor;
        invalidator.ProviderChanged += InvalidateProvider;
    }

    public async Task<LlmProviderRuntimeConfig> GetProviderAsync(string providerKey, CancellationToken ct = default)
    {
        var provider = LlmProviderKeys.Normalize(providerKey);
        if (!LlmProviderKeys.IsKnown(provider))
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerKey),
                providerKey,
                $"Unknown LLM provider '{providerKey}'. Expected one of: {string.Join(", ", LlmProviderKeys.All)}.");
        }

        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(provider, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Config;
        }

        var resolved = await ResolveUncachedAsync(provider, ct);
        cache[provider] = new CachedProviderConfig(resolved, now.Add(CacheTtl));
        return resolved;
    }

    private async Task<LlmProviderRuntimeConfig> ResolveUncachedAsync(string provider, CancellationToken ct)
    {
        var fallback = LlmProviderRuntimeConfig.FromOptions(provider, optionsAccessor.Value);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<ILlmConfigRepository>();
        if (repository is null)
        {
            return fallback;
        }

        var config = await repository.GetProviderAsync(provider, ct);
        if (config is null)
        {
            return fallback;
        }

        var token = await repository.GetProviderTokenAsync(provider, ct);
        return fallback with
        {
            HasDbConfig = true,
            HasDbToken = !string.IsNullOrWhiteSpace(token),
            ApiKey = string.IsNullOrWhiteSpace(token) ? fallback.ApiKey : token!,
            EndpointUrl = string.IsNullOrWhiteSpace(config.EndpointUrl) ? fallback.EndpointUrl : config.EndpointUrl,
            ApiVersion = string.IsNullOrWhiteSpace(config.ApiVersion) ? fallback.ApiVersion : config.ApiVersion,
            Models = config.Models.Count == 0 ? fallback.Models : config.Models
        };
    }

    private void InvalidateProvider(string providerKey)
    {
        cache.TryRemove(LlmProviderKeys.Normalize(providerKey), out _);
    }

    private sealed record CachedProviderConfig(LlmProviderRuntimeConfig Config, DateTimeOffset ExpiresAtUtc);
}

public sealed record LlmProviderRuntimeConfig(
    string ProviderKey,
    string ApiKey,
    string? EndpointUrl,
    string? ApiVersion,
    string Model,
    IReadOnlyList<string> Models,
    bool HasDbConfig,
    bool HasDbToken)
{
    public static LlmProviderRuntimeConfig FromOptions(string providerKey, AnalysisOptions options)
    {
        var provider = LlmProviderKeys.Normalize(providerKey);
        return provider switch
        {
            LlmProviderKeys.Anthropic => Create(
                provider,
                options.Anthropic.ApiKey,
                endpointUrl: null,
                apiVersion: options.Anthropic.Version,
                model: options.Model),
            LlmProviderKeys.OpenAi => Create(
                provider,
                options.OpenAi.ApiKey,
                options.OpenAi.BaseUrl,
                apiVersion: null,
                model: FirstNonEmpty(options.OpenAi.Model, options.Model)),
            LlmProviderKeys.LmStudio => Create(
                provider,
                options.LmStudio.ApiKey,
                options.LmStudio.BaseUrl,
                apiVersion: null,
                model: options.LmStudio.Model),
            _ => throw new ArgumentOutOfRangeException(nameof(providerKey), providerKey, "Unknown LLM provider.")
        };
    }

    private static LlmProviderRuntimeConfig Create(
        string provider,
        string apiKey,
        string? endpointUrl,
        string? apiVersion,
        string model)
    {
        var normalizedModel = FirstNonEmpty(model);
        var models = string.IsNullOrWhiteSpace(normalizedModel)
            ? Array.Empty<string>()
            : [normalizedModel];

        return new LlmProviderRuntimeConfig(
            provider,
            apiKey,
            endpointUrl,
            apiVersion,
            normalizedModel,
            models,
            HasDbConfig: false,
            HasDbToken: false);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
