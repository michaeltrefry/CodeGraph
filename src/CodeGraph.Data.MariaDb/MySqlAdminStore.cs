using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlAdminStore(CodeGraphDbContext db) : IAdminStore
{
    public async Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync()
        => await db.AdminUsers.AsNoTracking().OrderBy(a => a.Username).ToListAsync();

    public async Task<bool> IsAdminAsync(string username)
        => await db.AdminUsers.AsNoTracking().AnyAsync(a => a.Username == username);

    public async Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTime.UtcNow;
        }

        db.AdminUsers.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> RemoveAdminUserAsync(string username)
    {
        var entity = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (entity is null)
        {
            return false;
        }

        db.AdminUsers.Remove(entity);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync()
        => await db.SettingsOverrides.AsNoTracking().OrderByDescending(s => s.Id).FirstOrDefaultAsync();

    public async Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity)
    {
        var existing = await db.SettingsOverrides.OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        if (existing is null)
        {
            db.SettingsOverrides.Add(entity);
        }
        else
        {
            existing.SettingsJson = entity.SettingsJson;
            existing.UpdatedBy = entity.UpdatedBy;
            existing.UpdatedAt = entity.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AgentPromptOverrideEntity>> ListPromptOverridesAsync()
        => await db.AgentPromptOverrides.AsNoTracking().OrderBy(p => p.PromptKey).ToListAsync();

    public async Task<AgentPromptOverrideEntity?> GetPromptOverrideAsync(string promptKey)
        => await db.AgentPromptOverrides.AsNoTracking().FirstOrDefaultAsync(p => p.PromptKey == promptKey);

    public async Task UpsertPromptOverrideAsync(AgentPromptOverrideEntity entity)
    {
        var existing = await db.AgentPromptOverrides.FirstOrDefaultAsync(p => p.PromptKey == entity.PromptKey);
        if (existing is null)
        {
            db.AgentPromptOverrides.Add(entity);
        }
        else
        {
            existing.PromptText = entity.PromptText;
            existing.UpdatedBy = entity.UpdatedBy;
            existing.UpdatedAt = entity.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<bool> DeletePromptOverrideAsync(string promptKey)
    {
        var existing = await db.AgentPromptOverrides.FirstOrDefaultAsync(p => p.PromptKey == promptKey);
        if (existing is null)
        {
            return false;
        }

        db.AgentPromptOverrides.Remove(existing);
        await db.SaveChangesAsync();
        return true;
    }
}
