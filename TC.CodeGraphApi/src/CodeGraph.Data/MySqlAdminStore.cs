using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data;

public class MySqlAdminStore(CodeGraphDbContext db) : IAdminStore
{
    // ── Admin Users ──

    public async Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync()
        => await db.AdminUsers.OrderBy(a => a.Username).ToListAsync();

    public async Task<bool> IsAdminAsync(string username)
        => await db.AdminUsers.AnyAsync(a => a.Username == username);

    public async Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity)
    {
        db.AdminUsers.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> RemoveAdminUserAsync(string username)
    {
        var entity = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (entity is null) return false;

        db.AdminUsers.Remove(entity);
        await db.SaveChangesAsync();
        return true;
    }

    // ── Settings Overrides ──

    public async Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync()
        => await db.SettingsOverrides.OrderByDescending(s => s.Id).FirstOrDefaultAsync();

    public async Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity)
    {
        var existing = await db.SettingsOverrides.OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        if (existing is not null)
        {
            existing.SettingsJson = entity.SettingsJson;
            existing.UpdatedBy = entity.UpdatedBy;
            existing.UpdatedAt = entity.UpdatedAt;
        }
        else
        {
            db.SettingsOverrides.Add(entity);
        }
        await db.SaveChangesAsync();
    }
}
