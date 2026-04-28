using System.Globalization;
using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class LlmConfigRepository : ILlmConfigRepository
{
    private readonly CodeGraphDbContext db;
    private readonly IAesEncryptor encryptor;

    public LlmConfigRepository(CodeGraphDbContext db, IAesEncryptor encryptor)
    {
        this.db = db;
        this.encryptor = encryptor;
    }

    public async Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default)
    {
        var provider = LlmConfigKeys.RequireKnownProvider(providerKey);
        var keys = ProviderKeys(provider);
        var values = await GetEntriesAsync(keys, ct);
        var models = await db.LlmProviderModels.AsNoTracking()
            .Where(m => m.ProviderKey == provider)
            .OrderBy(m => m.DisplayOrder)
            .ThenBy(m => m.ModelId)
            .Select(m => m.ModelId)
            .ToListAsync(ct);

        if (values.Count == 0 && models.Count == 0)
        {
            return null;
        }

        return new LlmProviderConfig(
            provider,
            HasToken: TryGetValue(values, LlmConfigKeys.ProviderTokenEncrypted(provider), out var token)
                && !string.IsNullOrWhiteSpace(token.ConfigValue),
            EndpointUrl: GetString(values, LlmConfigKeys.ProviderEndpointUrl(provider)),
            ApiVersion: GetString(values, LlmConfigKeys.ProviderApiVersion(provider)),
            Models: models,
            UpdatedBy: Latest(values.Values)?.UpdatedBy,
            UpdatedAtUtc: Latest(values.Values)?.UpdatedAtUtc);
    }

    public async Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default)
    {
        var provider = LlmConfigKeys.RequireKnownProvider(providerKey);
        var key = LlmConfigKeys.ProviderTokenEncrypted(provider);
        LlmConfigKeys.RequireKnownKey(key);

        var encrypted = await db.LlmConfig.AsNoTracking()
            .Where(c => c.ConfigKey == key)
            .Select(c => c.ConfigValue)
            .SingleOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(encrypted)
            ? null
            : encryptor.Decrypt(encrypted);
    }

    public async Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default)
    {
        var provider = LlmConfigKeys.RequireKnownProvider(write.ProviderKey);
        if (write.Token is { Action: LlmProviderTokenActionKind.Replace })
        {
            if (string.IsNullOrWhiteSpace(write.Token.Value))
            {
                throw new ArgumentException("Replacing an LLM provider token requires a plaintext value.", nameof(write));
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await UpsertConfigAsync(LlmConfigKeys.ProviderEndpointUrl(provider), NormalizeOptional(write.EndpointUrl), write.UpdatedBy, ct);
        await UpsertConfigAsync(LlmConfigKeys.ProviderApiVersion(provider), NormalizeOptional(write.ApiVersion), write.UpdatedBy, ct);

        if (write.Token is { Action: LlmProviderTokenActionKind.Replace })
        {
            await UpsertConfigAsync(
                LlmConfigKeys.ProviderTokenEncrypted(provider),
                encryptor.Encrypt(write.Token.Value!),
                write.UpdatedBy,
                ct);
        }
        else if (write.Token is { Action: LlmProviderTokenActionKind.Clear })
        {
            await UpsertConfigAsync(LlmConfigKeys.ProviderTokenEncrypted(provider), null, write.UpdatedBy, ct);
        }

        if (write.Models is not null)
        {
            var existing = await db.LlmProviderModels
                .Where(m => m.ProviderKey == provider)
                .ToListAsync(ct);
            db.LlmProviderModels.RemoveRange(existing);

            db.LlmProviderModels.AddRange(NormalizeModelList(write.Models)
                .Select((model, index) => new LlmProviderModelEntity
                {
                    ProviderKey = provider,
                    ModelId = model,
                    DisplayOrder = index
                }));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default)
    {
        var values = await GetEntriesAsync(LlmConfigKeys.AnalysisKeys, ct);
        if (values.Count == 0)
        {
            return null;
        }

        return new LlmAnalysisConfig(
            DefaultProvider: GetString(values, LlmConfigKeys.AnalysisDefaultProvider),
            DefaultModel: GetString(values, LlmConfigKeys.AnalysisDefaultModel),
            MaxTokensPerAnalysis: GetInt(values, LlmConfigKeys.AnalysisMaxTokensPerAnalysis),
            MaxTokensPerSynthesis: GetInt(values, LlmConfigKeys.AnalysisMaxTokensPerSynthesis),
            MaxFileSizeKb: GetInt(values, LlmConfigKeys.AnalysisMaxFileSizeKb),
            MaxParallelAnalyses: GetInt(values, LlmConfigKeys.AnalysisMaxParallelAnalyses),
            MaxSourceChars: GetInt(values, LlmConfigKeys.AnalysisMaxSourceChars),
            UpdatedBy: Latest(values.Values)?.UpdatedBy,
            UpdatedAtUtc: Latest(values.Values)?.UpdatedAtUtc);
    }

    public async Task SetAnalysisAsync(LlmAnalysisWrite write, CancellationToken ct = default)
    {
        await SetSectionAsync(new Dictionary<string, string?>
        {
            [LlmConfigKeys.AnalysisDefaultProvider] = write.DefaultProvider,
            [LlmConfigKeys.AnalysisDefaultModel] = write.DefaultModel,
            [LlmConfigKeys.AnalysisMaxTokensPerAnalysis] = FormatInt(write.MaxTokensPerAnalysis),
            [LlmConfigKeys.AnalysisMaxTokensPerSynthesis] = FormatInt(write.MaxTokensPerSynthesis),
            [LlmConfigKeys.AnalysisMaxFileSizeKb] = FormatInt(write.MaxFileSizeKb),
            [LlmConfigKeys.AnalysisMaxParallelAnalyses] = FormatInt(write.MaxParallelAnalyses),
            [LlmConfigKeys.AnalysisMaxSourceChars] = FormatInt(write.MaxSourceChars)
        }, write.UpdatedBy, ct);
    }

    public async Task<LlmReviewConfig?> GetReviewAsync(CancellationToken ct = default)
    {
        var values = await GetEntriesAsync(LlmConfigKeys.ReviewKeys, ct);
        if (values.Count == 0)
        {
            return null;
        }

        return new LlmReviewConfig(
            DefaultProvider: GetString(values, LlmConfigKeys.ReviewDefaultProvider),
            DefaultModel: GetString(values, LlmConfigKeys.ReviewDefaultModel),
            MaxFilesToInspect: GetInt(values, LlmConfigKeys.ReviewMaxFilesToInspect),
            MaxSourceCharsPerFile: GetInt(values, LlmConfigKeys.ReviewMaxSourceCharsPerFile),
            MaxInspectionPasses: GetInt(values, LlmConfigKeys.ReviewMaxInspectionPasses),
            MaxFindings: GetInt(values, LlmConfigKeys.ReviewMaxFindings),
            UpdatedBy: Latest(values.Values)?.UpdatedBy,
            UpdatedAtUtc: Latest(values.Values)?.UpdatedAtUtc);
    }

    public async Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default)
    {
        await SetSectionAsync(new Dictionary<string, string?>
        {
            [LlmConfigKeys.ReviewDefaultProvider] = write.DefaultProvider,
            [LlmConfigKeys.ReviewDefaultModel] = write.DefaultModel,
            [LlmConfigKeys.ReviewMaxFilesToInspect] = FormatInt(write.MaxFilesToInspect),
            [LlmConfigKeys.ReviewMaxSourceCharsPerFile] = FormatInt(write.MaxSourceCharsPerFile),
            [LlmConfigKeys.ReviewMaxInspectionPasses] = FormatInt(write.MaxInspectionPasses),
            [LlmConfigKeys.ReviewMaxFindings] = FormatInt(write.MaxFindings)
        }, write.UpdatedBy, ct);
    }

    public async Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default)
    {
        var values = await GetEntriesAsync(LlmConfigKeys.AssistantKeys, ct);
        if (values.Count == 0)
        {
            return null;
        }

        return new LlmAssistantConfig(
            DefaultProvider: GetString(values, LlmConfigKeys.AssistantDefaultProvider),
            DefaultModel: GetString(values, LlmConfigKeys.AssistantDefaultModel),
            MaxTokens: GetInt(values, LlmConfigKeys.AssistantMaxTokens),
            MaxTurns: GetInt(values, LlmConfigKeys.AssistantMaxTurns),
            UpdatedBy: Latest(values.Values)?.UpdatedBy,
            UpdatedAtUtc: Latest(values.Values)?.UpdatedAtUtc);
    }

    public async Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default)
    {
        await SetSectionAsync(new Dictionary<string, string?>
        {
            [LlmConfigKeys.AssistantDefaultProvider] = write.DefaultProvider,
            [LlmConfigKeys.AssistantDefaultModel] = write.DefaultModel,
            [LlmConfigKeys.AssistantMaxTokens] = FormatInt(write.MaxTokens),
            [LlmConfigKeys.AssistantMaxTurns] = FormatInt(write.MaxTurns)
        }, write.UpdatedBy, ct);
    }

    private async Task SetSectionAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var (key, value) in values)
        {
            await UpsertConfigAsync(key, NormalizeOptional(value), updatedBy, ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<Dictionary<string, LlmConfigEntryEntity>> GetEntriesAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var keyList = keys.ToList();
        foreach (var key in keyList)
        {
            LlmConfigKeys.RequireKnownKey(key);
        }

        return await db.LlmConfig.AsNoTracking()
            .Where(c => keyList.Contains(c.ConfigKey))
            .ToDictionaryAsync(c => c.ConfigKey, StringComparer.OrdinalIgnoreCase, ct);
    }

    private async Task UpsertConfigAsync(string key, string? value, string? updatedBy, CancellationToken ct)
    {
        LlmConfigKeys.RequireKnownKey(key);
        var entity = await db.LlmConfig.FindAsync([key], ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (entity is not null)
            {
                db.LlmConfig.Remove(entity);
            }

            return;
        }

        var now = DateTime.UtcNow;
        if (entity is null)
        {
            db.LlmConfig.Add(new LlmConfigEntryEntity
            {
                ConfigKey = key,
                ConfigValue = value,
                UpdatedBy = NormalizeOptional(updatedBy),
                UpdatedAtUtc = now
            });
            return;
        }

        entity.ConfigValue = value;
        entity.UpdatedBy = NormalizeOptional(updatedBy);
        entity.UpdatedAtUtc = now;
    }

    private static IReadOnlyList<string> ProviderKeys(string providerKey) =>
    [
        LlmConfigKeys.ProviderEndpointUrl(providerKey),
        LlmConfigKeys.ProviderApiVersion(providerKey),
        LlmConfigKeys.ProviderTokenEncrypted(providerKey)
    ];

    private static IReadOnlyList<string> NormalizeModelList(IEnumerable<string> models) =>
        models
            .Select(model => model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool TryGetValue(
        IReadOnlyDictionary<string, LlmConfigEntryEntity> values,
        string key,
        out LlmConfigEntryEntity entry) =>
        values.TryGetValue(key, out entry!);

    private static string? GetString(IReadOnlyDictionary<string, LlmConfigEntryEntity> values, string key) =>
        TryGetValue(values, key, out var entry) ? entry.ConfigValue : null;

    private static int? GetInt(IReadOnlyDictionary<string, LlmConfigEntryEntity> values, string key)
    {
        var raw = GetString(values, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static LlmConfigEntryEntity? Latest(IEnumerable<LlmConfigEntryEntity> entries) =>
        entries
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .FirstOrDefault();

    private static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
