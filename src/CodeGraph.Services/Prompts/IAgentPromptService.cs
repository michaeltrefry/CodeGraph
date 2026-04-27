namespace CodeGraph.Services.Prompts;

public interface IAgentPromptService
{
    Task<IReadOnlyList<AgentPromptAdminModel>> ListAsync();
    Task<AgentPromptAdminModel?> GetAsync(string promptKey);
    Task<AgentPromptAdminModel?> SaveOverrideAsync(string promptKey, string promptText, string updatedBy);
    Task<bool?> ResetOverrideAsync(string promptKey);
    Task<string> GetEffectivePromptAsync(string promptKey);
}
