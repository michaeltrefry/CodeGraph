using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public class Neo4jAdminStore(Neo4jSessionFactory sessionFactory) : IAdminStore
{
    // ── Admin Users ──

    public async Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (a:AdminUser) RETURN a ORDER BY a.username");
            var results = new List<AdminUserEntity>();
            await foreach (var record in cursor)
            {
                var node = record["a"].As<INode>();
                results.Add(MapAdminUser(node));
            }
            return results;
        });
    }

    public async Task<bool> IsAdminAsync(string username)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (a:AdminUser {username: $username}) RETURN count(a) AS cnt",
                new { username });
            await cursor.FetchAsync();
            return cursor.Current["cnt"].As<int>() > 0;
        });
    }

    public async Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "AdminUser");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (a:AdminUser {
                    appId: $id, username: $username, createdAt: $createdAt
                })
                """,
                new { id = entity.Id, username = entity.Username, createdAt = entity.CreatedAt });
        });
        return entity;
    }

    public async Task<bool> RemoveAdminUserAsync(string username)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (a:AdminUser {username: $username})
                WITH a, count(a) AS cnt
                DELETE a
                RETURN cnt
                """,
                new { username });
            await cursor.FetchAsync();
            return cursor.Current["cnt"].As<int>() > 0;
        });
    }

    // ── Settings Overrides ──

    public async Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:SettingsOverride) RETURN s ORDER BY s.appId DESC LIMIT 1");
            return await cursor.FetchAsync() ? MapSettings(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Try to update existing
            var cursor = await tx.RunAsync("""
                MATCH (s:SettingsOverride)
                WITH s ORDER BY s.appId DESC LIMIT 1
                SET s.settingsJson = $settingsJson,
                    s.updatedBy = $updatedBy,
                    s.updatedAt = $updatedAt
                RETURN s.appId AS id
                """,
                new
                {
                    settingsJson = entity.SettingsJson,
                    updatedBy = entity.UpdatedBy,
                    updatedAt = entity.UpdatedAt
                });

            if (!await cursor.FetchAsync())
            {
                // No existing — create new
                var id = await NextId(tx, "SettingsOverride");
                await tx.RunAsync("""
                    CREATE (s:SettingsOverride {
                        appId: $id, settingsJson: $settingsJson,
                        updatedBy: $updatedBy, updatedAt: $updatedAt
                    })
                    """,
                    new
                    {
                        id,
                        settingsJson = entity.SettingsJson,
                        updatedBy = entity.UpdatedBy,
                        updatedAt = entity.UpdatedAt
                    });
            }
        });
    }

    // ── ID Generation ──

    private static async Task<long> NextId(IAsyncSession session, string label)
    {
        return await session.ExecuteWriteAsync(async tx => await NextId(tx, label));
    }

    private static async Task<long> NextId(IAsyncQueryRunner tx, string label)
    {
        var cursor = await tx.RunAsync("""
            MERGE (c:IdCounter {label: $label})
            ON CREATE SET c.current = 1
            ON MATCH SET c.current = c.current + 1
            RETURN c.current AS id
            """,
            new { label });
        await cursor.FetchAsync();
        return cursor.Current["id"].As<long>();
    }

    // ── Mapping ──

    private static AdminUserEntity MapAdminUser(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Username = node["username"].As<string>(),
        CreatedAt = GetDateTime(node, "createdAt")
    };

    private static SettingsOverrideEntity MapSettings(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        SettingsJson = node["settingsJson"].As<string>(),
        UpdatedBy = node.Properties.TryGetValue("updatedBy", out var ub) ? ub.As<string>() : "",
        UpdatedAt = GetDateTime(node, "updatedAt")
    };

    private static DateTime GetDateTime(INode node, string key)
    {
        if (!node.Properties.TryGetValue(key, out var val) || val is null)
            return DateTime.MinValue;
        if (val is LocalDateTime ldt) return ldt.ToDateTime();
        if (val is ZonedDateTime zdt) return zdt.ToDateTimeOffset().UtcDateTime;
        if (val is string s && DateTime.TryParse(s, out var dt)) return dt;
        return DateTime.MinValue;
    }
}
