using TC.CodeGraphApi.Data;

namespace TC.CodeGraphApi.Services;

public class AdminUserService(IAdminStore store) : IAdminUserService
{
    public async Task<IReadOnlyList<string>> ListAsync()
    {
        var users = await store.ListAdminUsersAsync();
        return users.Select(a => a.Username).ToList();
    }

    public async Task<bool> IsAdminAsync(string username)
    {
        return await store.IsAdminAsync(username);
    }

    public async Task<bool> AddAsync(string username)
    {
        if (await store.IsAdminAsync(username))
            return false;

        await store.AddAdminUserAsync(new AdminUserEntity
        {
            Username = username,
            CreatedAt = DateTime.UtcNow
        });
        return true;
    }

    public async Task<bool> RemoveAsync(string username)
    {
        return await store.RemoveAdminUserAsync(username);
    }
}
