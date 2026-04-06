using Dapper;

namespace CodeGraph.Data;

public partial class MySqlGraphStore
{
    public async Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync()
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<ExclusionRuleEntity>(
            "SELECT * FROM exclusion_rules ORDER BY target_type, target_value");
        return results.ToList();
    }

    public async Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id)
    {
        await using var conn = await GetOpenConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<ExclusionRuleEntity>(
            "SELECT * FROM exclusion_rules WHERE id = @id", new { id });
    }

    public async Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule)
    {
        await using var conn = await GetOpenConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO exclusion_rules (target_type, target_value, exclusion_type, reason, created_by)
            VALUES (@TargetType, @TargetValue, @ExclusionType, @Reason, @CreatedBy);
            SELECT LAST_INSERT_ID();
            """, rule);
        rule.Id = id;
        return rule;
    }

    public async Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason)
    {
        await using var conn = await GetOpenConnectionAsync();
        var affected = await conn.ExecuteAsync("""
            UPDATE exclusion_rules
            SET exclusion_type = @exclusionType, reason = @reason
            WHERE id = @id
            """, new { id, exclusionType, reason });

        if (affected == 0) return null;
        return await GetExclusionRuleAsync(id);
    }

    public async Task<bool> DeleteExclusionRuleAsync(long id)
    {
        await using var conn = await GetOpenConnectionAsync();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM exclusion_rules WHERE id = @id", new { id });
        return affected > 0;
    }

    public async Task<HashSet<string>> GetSecretFilePathsAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var paths = await conn.QueryAsync<string>(
            "SELECT DISTINCT file_path FROM security_findings WHERE project = @project AND category = 'secret' AND file_path IS NOT NULL",
            new { project });
        return paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
