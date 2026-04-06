namespace TC.CodeGraphApi.Data;

/// <summary>
/// Storage abstraction for admin users and settings overrides.
/// </summary>
public interface IAdminStore
{
    // ── Admin Users ──

    Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync();
    Task<bool> IsAdminAsync(string username);
    Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity);
    Task<bool> RemoveAdminUserAsync(string username);

    // ── Settings Overrides ──

    Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync();
    Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity);
}
