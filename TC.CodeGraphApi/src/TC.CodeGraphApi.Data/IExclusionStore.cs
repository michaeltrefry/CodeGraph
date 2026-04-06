namespace TC.CodeGraphApi.Data;

public interface IExclusionStore
{
    Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync();
    Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id);
    Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule);
    Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason);
    Task<bool> DeleteExclusionRuleAsync(long id);
    Task<HashSet<string>> GetSecretFilePathsAsync(string project);
}
