using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlExclusionStore(CodeGraphDbContext db) : IExclusionStore
{
    public async Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync()
        => await db.ExclusionRules.AsNoTracking()
            .OrderBy(r => r.TargetType)
            .ThenBy(r => r.TargetValue)
            .ToListAsync();

    public async Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id)
        => await db.ExclusionRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);

    public async Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule)
    {
        db.ExclusionRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    public async Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason)
    {
        var rule = await db.ExclusionRules.FindAsync(id);
        if (rule is null)
        {
            return null;
        }

        rule.ExclusionType = exclusionType;
        rule.Reason = reason;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return rule;
    }

    public async Task<bool> DeleteExclusionRuleAsync(long id)
    {
        var rule = await db.ExclusionRules.FindAsync(id);
        if (rule is null)
        {
            return false;
        }

        db.ExclusionRules.Remove(rule);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<HashSet<string>> GetSecretFilePathsAsync(string project)
    {
        var paths = await db.SecurityFindings.AsNoTracking()
            .Where(f => f.Project == project && f.Category == "secret" && f.FilePath != null)
            .Select(f => f.FilePath!)
            .Distinct()
            .ToListAsync();

        return paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
