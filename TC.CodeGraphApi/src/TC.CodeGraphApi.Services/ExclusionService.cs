using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;

namespace TC.CodeGraphApi.Services;

public interface IExclusionService
{
    /// <summary>
    /// Returns "complete", "no_analysis", or null.
    /// Checks repo-level rules first, then group-level (any segment match).
    /// </summary>
    Task<string?> GetExclusionTypeAsync(string repoName, string? gitLabGroup);

    Task<HashSet<string>> GetSecretFilePathsAsync(string project);

    Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync();
    Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy);
    Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason);
    Task<bool> DeleteRuleAsync(long id);

    Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups);
}

public class ExclusionService(
    IExclusionStore exclusionStore,
    ILogger<ExclusionService> logger)
    : IExclusionService
{
    private volatile IReadOnlyList<ExclusionRuleEntity>? _cachedRules;
    private readonly object _cacheLock = new();

    public async Task<string?> GetExclusionTypeAsync(string repoName, string? gitLabGroup)
    {
        var rules = await GetCachedRulesAsync();

        // Check repo-level rule first (exact match, case-insensitive)
        var repoRule = rules.FirstOrDefault(r =>
            r.TargetType == "repository" &&
            r.TargetValue.Equals(repoName, StringComparison.OrdinalIgnoreCase));

        if (repoRule is not null)
            return repoRule.ExclusionType;

        // Check group-level rules (any namespace segment match)
        if (!string.IsNullOrWhiteSpace(gitLabGroup))
        {
            var segments = gitLabGroup.ToLowerInvariant().Split('/');
            var groupRules = rules
                .Where(r => r.TargetType == "group")
                .ToList();

            foreach (var rule in groupRules)
            {
                if (segments.Any(seg => seg.Equals(rule.TargetValue, StringComparison.OrdinalIgnoreCase)))
                    return rule.ExclusionType;
            }
        }

        return null;
    }

    public async Task<HashSet<string>> GetSecretFilePathsAsync(string project)
    {
        return await exclusionStore.GetSecretFilePathsAsync(project);
    }

    public async Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync()
    {
        return await exclusionStore.ListExclusionRulesAsync();
    }

    public async Task<ExclusionRuleEntity> CreateRuleAsync(
        string targetType, string targetValue, string exclusionType, string? reason, string createdBy)
    {
        var rule = new ExclusionRuleEntity
        {
            TargetType = targetType,
            TargetValue = targetValue,
            ExclusionType = exclusionType,
            Reason = reason,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await exclusionStore.CreateExclusionRuleAsync(rule);
        InvalidateCache();
        logger.LogInformation("Created exclusion rule: {Type} '{Value}' → {Exclusion}",
            targetType, targetValue, exclusionType);
        return created;
    }

    public async Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason)
    {
        var updated = await exclusionStore.UpdateExclusionRuleAsync(id, exclusionType, reason);
        if (updated is not null)
        {
            InvalidateCache();
            logger.LogInformation("Updated exclusion rule {Id}: → {Exclusion}", id, exclusionType);
        }
        return updated;
    }

    public async Task<bool> DeleteRuleAsync(long id)
    {
        var deleted = await exclusionStore.DeleteExclusionRuleAsync(id);
        if (deleted)
        {
            InvalidateCache();
            logger.LogInformation("Deleted exclusion rule {Id}", id);
        }
        return deleted;
    }

    public async Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups)
    {
        if (excludedGroups.Count == 0) return;

        var existing = await exclusionStore.ListExclusionRulesAsync();
        if (existing.Count > 0) return;

        foreach (var group in excludedGroups)
        {
            await exclusionStore.CreateExclusionRuleAsync(new ExclusionRuleEntity
            {
                TargetType = "group",
                TargetValue = group,
                ExclusionType = "complete",
                Reason = "Migrated from appsettings.json ExcludedGroups",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        InvalidateCache();
        logger.LogInformation("Seeded {Count} exclusion rules from config", excludedGroups.Count);
    }

    private async Task<IReadOnlyList<ExclusionRuleEntity>> GetCachedRulesAsync()
    {
        if (_cachedRules is not null) return _cachedRules;

        var rules = await exclusionStore.ListExclusionRulesAsync();
        lock (_cacheLock)
        {
            _cachedRules ??= rules;
        }
        return _cachedRules;
    }

    private void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedRules = null;
        }
    }
}
