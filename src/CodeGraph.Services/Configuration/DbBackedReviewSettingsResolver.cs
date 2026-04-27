using System.Collections.Concurrent;
using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Configuration;

public interface IDbBackedReviewSettingsResolver
{
    Task<LlmReviewRuntimeConfig> GetReviewAsync(CancellationToken ct = default);
}

public sealed class DbBackedReviewSettingsResolver : IDbBackedReviewSettingsResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const string CacheKey = "review";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AnalysisOptions> optionsAccessor;
    private readonly ILlmCatalogValidator catalogValidator;
    private readonly ConcurrentDictionary<string, CachedReviewConfig> cache = new(StringComparer.OrdinalIgnoreCase);

    public DbBackedReviewSettingsResolver(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisOptions> optionsAccessor,
        ILlmCatalogValidator catalogValidator,
        ILlmConfigInvalidator invalidator)
    {
        this.scopeFactory = scopeFactory;
        this.optionsAccessor = optionsAccessor;
        this.catalogValidator = catalogValidator;
        invalidator.ReviewChanged += InvalidateReview;
    }

    public async Task<LlmReviewRuntimeConfig> GetReviewAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(CacheKey, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Config;
        }

        var resolved = await ResolveUncachedAsync(ct);
        cache[CacheKey] = new CachedReviewConfig(resolved, now.Add(CacheTtl));
        return resolved;
    }

    private async Task<LlmReviewRuntimeConfig> ResolveUncachedAsync(CancellationToken ct)
    {
        var fallback = LlmReviewRuntimeConfig.FromOptions(optionsAccessor.Value);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<ILlmConfigRepository>();
        if (repository is null)
        {
            return fallback;
        }

        var config = await repository.GetReviewAsync(ct);
        if (config is null)
        {
            return fallback;
        }

        var resolved = fallback with
        {
            DefaultProvider = FirstNonEmpty(config.DefaultProvider, fallback.DefaultProvider),
            DefaultModel = FirstNonEmpty(config.DefaultModel, fallback.DefaultModel),
            MaxFilesToInspect = config.MaxFilesToInspect ?? fallback.MaxFilesToInspect,
            MaxSourceCharsPerFile = config.MaxSourceCharsPerFile ?? fallback.MaxSourceCharsPerFile,
            MaxInspectionPasses = config.MaxInspectionPasses ?? fallback.MaxInspectionPasses,
            MaxFindings = config.MaxFindings ?? fallback.MaxFindings,
            UpdatedBy = config.UpdatedBy,
            UpdatedAtUtc = config.UpdatedAtUtc,
            HasDbConfig = true
        };

        await catalogValidator.EnsureProviderModelAsync(resolved.DefaultProvider, resolved.DefaultModel, ct);
        return resolved;
    }

    private void InvalidateReview()
    {
        cache.TryRemove(CacheKey, out _);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record CachedReviewConfig(LlmReviewRuntimeConfig Config, DateTimeOffset ExpiresAtUtc);
}

public sealed record LlmReviewRuntimeConfig(
    string DefaultProvider,
    string DefaultModel,
    int MaxFilesToInspect,
    int MaxSourceCharsPerFile,
    int MaxInspectionPasses,
    int MaxFindings,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    bool HasDbConfig)
{
    public static LlmReviewRuntimeConfig FromOptions(AnalysisOptions options) =>
        new(
            string.IsNullOrWhiteSpace(options.DefaultProvider) ? LlmProviderKeys.Anthropic : options.DefaultProvider.Trim(),
            string.IsNullOrWhiteSpace(options.Review.Model) ? "" : options.Review.Model.Trim(),
            options.Review.MaxFilesToInspect,
            options.Review.MaxSourceCharsPerFile,
            options.Review.MaxInspectionPasses,
            options.Review.MaxFindings,
            UpdatedBy: null,
            UpdatedAtUtc: null,
            HasDbConfig: false);
}
