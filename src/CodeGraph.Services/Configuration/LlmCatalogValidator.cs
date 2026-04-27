using System.Collections.Concurrent;
using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Services.Configuration;

public interface ILlmCatalogValidator
{
    Task<LlmCatalogValidationResult> ValidateProviderModelAsync(
        string? provider,
        string? model,
        CancellationToken ct = default);

    Task EnsureProviderModelAsync(
        string? provider,
        string? model,
        CancellationToken ct = default);
}

public sealed class LlmCatalogValidator : ILlmCatalogValidator
{
    public const string DefaultProviderField = "default_provider";
    public const string DefaultModelField = "default_model";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly Func<string, CancellationToken, Task<LlmProviderConfig?>> getProviderAsync;
    private readonly ConcurrentDictionary<string, CachedValidation> cache = new(StringComparer.OrdinalIgnoreCase);

    public LlmCatalogValidator(IServiceScopeFactory scopeFactory, ILlmConfigInvalidator invalidator)
        : this(async (provider, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ILlmConfigRepository>();
            return await repository.GetProviderAsync(provider, ct);
        }, invalidator)
    {
    }

    internal LlmCatalogValidator(
        Func<string, CancellationToken, Task<LlmProviderConfig?>> getProviderAsync,
        ILlmConfigInvalidator invalidator)
    {
        this.getProviderAsync = getProviderAsync;
        invalidator.ProviderChanged += InvalidateProvider;
    }

    public async Task<LlmCatalogValidationResult> ValidateProviderModelAsync(
        string? provider,
        string? model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !LlmProviderKeys.IsKnown(provider))
        {
            return LlmCatalogValidationResult.Failure(
                DefaultProviderField,
                $"provider must be one of: {string.Join(", ", LlmProviderKeys.All)}");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return LlmCatalogValidationResult.Failure(DefaultModelField, "model must be configured");
        }

        var normalizedProvider = LlmProviderKeys.Normalize(provider);
        var normalizedModel = model.Trim();
        var key = CacheKey(normalizedProvider, normalizedModel);
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Result;
        }

        var result = await ValidateKnownProviderModelAsync(normalizedProvider, normalizedModel, ct);
        cache[key] = new CachedValidation(result, now.Add(CacheTtl));
        return result;
    }

    public async Task EnsureProviderModelAsync(
        string? provider,
        string? model,
        CancellationToken ct = default)
    {
        var result = await ValidateProviderModelAsync(provider, model, ct);
        if (!result.IsValid)
        {
            throw new LlmCatalogValidationException(result);
        }
    }

    private async Task<LlmCatalogValidationResult> ValidateKnownProviderModelAsync(
        string provider,
        string model,
        CancellationToken ct)
    {
        var providerConfig = await getProviderAsync(provider, ct);
        if (providerConfig?.HasToken != true)
        {
            return LlmCatalogValidationResult.Failure(
                DefaultProviderField,
                $"provider {provider} is not configured (set the API token first)");
        }

        if (!providerConfig.Models.Contains(model, StringComparer.Ordinal))
        {
            return LlmCatalogValidationResult.Failure(
                DefaultModelField,
                $"model {model} is not in the configured model list for {provider}");
        }

        return LlmCatalogValidationResult.Success;
    }

    private void InvalidateProvider(string providerKey)
    {
        var prefix = $"{LlmProviderKeys.Normalize(providerKey)}\u001f";
        foreach (var key in cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            cache.TryRemove(key, out _);
        }
    }

    private static string CacheKey(string provider, string model) => $"{provider}\u001f{model}";

    private sealed record CachedValidation(LlmCatalogValidationResult Result, DateTimeOffset ExpiresAtUtc);
}

public sealed record LlmCatalogValidationResult(IReadOnlyList<LlmCatalogValidationError> Errors)
{
    public static readonly LlmCatalogValidationResult Success = new([]);

    public bool IsValid => Errors.Count == 0;

    public static LlmCatalogValidationResult Failure(string field, string message) =>
        new([new LlmCatalogValidationError(field, message)]);
}

public sealed record LlmCatalogValidationError(string Field, string Message);

public sealed class LlmCatalogValidationException : InvalidOperationException
{
    public LlmCatalogValidationException(LlmCatalogValidationResult result)
        : base(string.Join("; ", result.Errors.Select(error => $"{error.Field}: {error.Message}")))
    {
        Result = result;
    }

    public LlmCatalogValidationResult Result { get; }
}
