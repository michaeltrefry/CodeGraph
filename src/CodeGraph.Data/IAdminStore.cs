namespace CodeGraph.Data;

public interface IAdminStore
{
    Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync();
    Task<bool> IsAdminAsync(string username);
    Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity);
    Task<bool> RemoveAdminUserAsync(string username);

    Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync();
    Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity);

    Task<IReadOnlyList<AgentPromptOverrideEntity>> ListPromptOverridesAsync();
    Task<AgentPromptOverrideEntity?> GetPromptOverrideAsync(string promptKey);
    Task UpsertPromptOverrideAsync(AgentPromptOverrideEntity entity);
    Task<bool> DeletePromptOverrideAsync(string promptKey);
}
