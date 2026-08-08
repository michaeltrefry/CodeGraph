using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public class Neo4jJobScheduleStore(Neo4jSessionFactory sessionFactory) : IJobScheduleStore
{
    public async Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule)
                RETURN s
                ORDER BY s.name, s.appId
                """);

            var results = new List<JobScheduleEntity>();
            await foreach (var record in cursor)
                results.Add(MapSchedule(record["s"].As<INode>()));
            return results;
        });
    }

    public async Task<JobScheduleEntity?> GetScheduleByIdAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                RETURN s
                """,
                new { id });

            return await cursor.FetchAsync() ? MapSchedule(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<JobScheduleEntity?> GetScheduleByNameAsync(string name)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {name: $name})
                RETURN s
                """,
                new { name });

            return await cursor.FetchAsync() ? MapSchedule(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "JobSchedule");

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (s:JobSchedule {
                    appId: $id,
                    name: $name,
                    jobType: $jobType,
                    isEnabled: $isEnabled,
                    cronExpression: $cronExpression,
                    timeZoneId: $timeZoneId,
                    argsJson: $argsJson,
                    nextRunUtc: $nextRunUtc,
                    scheduleRevision: $scheduleRevision,
                    lastRunStartedUtc: $lastRunStartedUtc,
                    lastRunCompletedUtc: $lastRunCompletedUtc,
                    lastRunStatus: $lastRunStatus,
                    lastError: $lastError,
                    leaseAcquiredUtc: $leaseAcquiredUtc,
                    leaseOwner: $leaseOwner,
                    leaseExpiresUtc: $leaseExpiresUtc,
                    createdAtUtc: $createdAtUtc,
                    updatedAtUtc: $updatedAtUtc
                })
                """,
                ScheduleParams(entity));
        });

        return entity;
    }

    public async Task<bool> UpdateScheduleAsync(JobScheduleEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE coalesce(s.scheduleRevision, 0) = $scheduleRevision
                SET s.name = $name,
                    s.jobType = $jobType,
                    s.isEnabled = $isEnabled,
                    s.cronExpression = $cronExpression,
                    s.timeZoneId = $timeZoneId,
                    s.argsJson = $argsJson,
                    s.nextRunUtc = $nextRunUtc,
                    s.scheduleRevision = $scheduleRevision + 1,
                    s.updatedAtUtc = $updatedAtUtc
                RETURN count(s) AS updated
                """,
                ScheduleParams(entity));
            await cursor.FetchAsync();
            return cursor.Current["updated"].As<long>() == 1;
        });
    }

    public async Task DeleteScheduleAsync(long id)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                DETACH DELETE s
                """,
                new { id });
        });
    }

    public async Task<JobScheduleEntity?> TryAcquireDueScheduleAsync(
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);

        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule)
                WHERE s.isEnabled = true
                  AND s.nextRunUtc <= $utcNow
                  AND (s.leaseExpiresUtc IS NULL OR s.leaseExpiresUtc <= $utcNow)
                WITH s
                ORDER BY s.nextRunUtc, s.appId
                LIMIT 1
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE s.isEnabled = true
                  AND s.nextRunUtc <= $utcNow
                  AND (s.leaseExpiresUtc IS NULL OR s.leaseExpiresUtc <= $utcNow)
                SET s.leaseOwner = $leaseOwner,
                    s.leaseAcquiredUtc = $utcNow,
                    s.leaseExpiresUtc = $leaseExpiresUtc,
                    s.updatedAtUtc = $utcNow
                RETURN s
                """,
                new
                {
                    utcNow,
                    leaseOwner,
                    leaseExpiresUtc
                });

            return await cursor.FetchAsync() ? MapSchedule(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<JobScheduleEntity?> TryAcquireScheduleAsync(
        long id,
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);

        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE s.leaseExpiresUtc IS NULL OR s.leaseExpiresUtc <= $utcNow
                SET s.leaseOwner = $leaseOwner,
                    s.leaseAcquiredUtc = $utcNow,
                    s.leaseExpiresUtc = $leaseExpiresUtc,
                    s.updatedAtUtc = $utcNow
                RETURN s
                """,
                new
                {
                    id,
                    utcNow,
                    leaseOwner,
                    leaseExpiresUtc
                });

            return await cursor.FetchAsync() ? MapSchedule(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<bool> RenewLeaseAsync(
        long id,
        DateTime utcNow,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE s.leaseOwner = $leaseToken
                  AND s.leaseExpiresUtc IS NOT NULL
                  AND s.leaseExpiresUtc > $utcNow
                SET s.leaseExpiresUtc = $leaseExpiresUtc,
                    s.updatedAtUtc = $utcNow
                RETURN count(s) AS updated
                """,
                new { id, utcNow, leaseToken, leaseExpiresUtc });
            await cursor.FetchAsync();
            return cursor.Current["updated"].As<long>() == 1;
        });
    }

    public async Task<bool> MarkRunStartedAsync(
        long id,
        DateTime startedAtUtc,
        DateTime fenceCheckedAtUtc,
        string leaseToken,
        CancellationToken ct = default)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE s.leaseOwner = $leaseToken
                  AND s.leaseExpiresUtc IS NOT NULL
                  AND s.leaseExpiresUtc > $fenceCheckedAtUtc
                SET s.lastRunStartedUtc = $startedAtUtc,
                    s.lastRunStatus = 'running',
                    s.lastError = null,
                    s.updatedAtUtc = $startedAtUtc
                RETURN count(s) AS updated
                """,
                new
                {
                    id,
                    startedAtUtc,
                    fenceCheckedAtUtc,
                    leaseToken
                });
            await cursor.FetchAsync();
            return cursor.Current["updated"].As<long>() == 1;
        });
    }

    public async Task<bool> MarkRunCompletedAsync(
        long id,
        DateTime completedAtUtc,
        DateTime? nextRunUtc,
        string status,
        string? error,
        DateTime fenceCheckedAtUtc,
        long acquiredScheduleRevision,
        string leaseToken,
        CancellationToken ct = default)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:JobSchedule {appId: $id})
                SET s._jobScheduleLeaseLock = true
                REMOVE s._jobScheduleLeaseLock
                WITH s
                WHERE s.leaseOwner = $leaseToken
                  AND s.leaseExpiresUtc IS NOT NULL
                  AND s.leaseExpiresUtc > $fenceCheckedAtUtc
                SET s.lastRunCompletedUtc = $completedAtUtc,
                    s.lastRunStatus = $status,
                    s.lastError = $error,
                    s.nextRunUtc = CASE
                        WHEN $nextRunUtc IS NOT NULL
                          AND coalesce(s.scheduleRevision, 0) = $acquiredScheduleRevision
                        THEN $nextRunUtc
                        ELSE s.nextRunUtc
                    END,
                    s.scheduleRevision = coalesce(s.scheduleRevision, 0) + 1,
                    s.leaseAcquiredUtc = null,
                    s.leaseOwner = null,
                    s.leaseExpiresUtc = null,
                    s.updatedAtUtc = $completedAtUtc
                RETURN count(s) AS updated
                """,
                new
                {
                    id,
                    completedAtUtc,
                    nextRunUtc,
                    status,
                    error,
                    fenceCheckedAtUtc,
                    acquiredScheduleRevision,
                    leaseToken
                });
            await cursor.FetchAsync();
            return cursor.Current["updated"].As<long>() == 1;
        });
    }

    private static object ScheduleParams(JobScheduleEntity entity) => new
    {
        id = entity.Id,
        name = entity.Name,
        jobType = entity.JobType,
        isEnabled = entity.IsEnabled,
        cronExpression = entity.CronExpression,
        timeZoneId = entity.TimeZoneId,
        argsJson = entity.ArgsJson,
        nextRunUtc = entity.NextRunUtc,
        scheduleRevision = entity.ScheduleRevision,
        lastRunStartedUtc = entity.LastRunStartedUtc,
        lastRunCompletedUtc = entity.LastRunCompletedUtc,
        lastRunStatus = entity.LastRunStatus,
        lastError = entity.LastError,
        leaseAcquiredUtc = entity.LeaseAcquiredUtc,
        leaseOwner = entity.LeaseOwner,
        leaseExpiresUtc = entity.LeaseExpiresUtc,
        createdAtUtc = entity.CreatedAtUtc,
        updatedAtUtc = entity.UpdatedAtUtc
    };

    private static JobScheduleEntity MapSchedule(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Name = node["name"].As<string>(),
        JobType = node["jobType"].As<string>(),
        IsEnabled = node["isEnabled"].As<bool>(),
        CronExpression = node["cronExpression"].As<string>(),
        TimeZoneId = node["timeZoneId"].As<string>(),
        ArgsJson = node.Properties.TryGetValue("argsJson", out var argsValue) && argsValue is not null
            ? argsValue.As<string>()
            : "{}",
        NextRunUtc = GetDateTime(node, "nextRunUtc"),
        ScheduleRevision = GetLong(node, "scheduleRevision"),
        LastRunStartedUtc = GetNullableDateTime(node, "lastRunStartedUtc"),
        LastRunCompletedUtc = GetNullableDateTime(node, "lastRunCompletedUtc"),
        LastRunStatus = GetStr(node, "lastRunStatus"),
        LastError = GetStr(node, "lastError"),
        LeaseAcquiredUtc = GetNullableDateTime(node, "leaseAcquiredUtc"),
        LeaseOwner = GetStr(node, "leaseOwner"),
        LeaseExpiresUtc = GetNullableDateTime(node, "leaseExpiresUtc"),
        CreatedAtUtc = GetDateTime(node, "createdAtUtc"),
        UpdatedAtUtc = GetDateTime(node, "updatedAtUtc")
    };

    private static string? GetStr(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<string>()
            : null;

    private static long GetLong(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<long>()
            : 0;

    private static DateTime GetDateTime(INode node, string key)
        => GetNullableDateTime(node, key) ?? DateTime.MinValue;

    private static DateTime? GetNullableDateTime(INode node, string key)
    {
        if (!node.Properties.TryGetValue(key, out var val) || val is null)
            return null;
        if (val is LocalDateTime ldt)
            return DateTime.SpecifyKind(ldt.ToDateTime(), DateTimeKind.Utc);
        if (val is ZonedDateTime zdt)
            return zdt.ToDateTimeOffset().UtcDateTime;
        if (val is string s && DateTime.TryParse(s, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return null;
    }

    private static async Task<long> NextId(IAsyncSession session, string label)
    {
        return await session.ExecuteWriteAsync(async tx =>
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
        });
    }
}
