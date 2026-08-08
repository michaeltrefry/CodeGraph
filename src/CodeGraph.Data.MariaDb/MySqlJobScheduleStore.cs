using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlJobScheduleStore(CodeGraphDbContext db) : IJobScheduleStore
{
    public async Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync()
        => await db.JobSchedules.AsNoTracking()
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .ToListAsync();

    public async Task<JobScheduleEntity?> GetScheduleByIdAsync(long id)
        => await db.JobSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);

    public async Task<JobScheduleEntity?> GetScheduleByNameAsync(string name)
        => await db.JobSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name);

    public async Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity)
    {
        var now = DateTime.UtcNow;
        if (entity.CreatedAtUtc == default)
        {
            entity.CreatedAtUtc = now;
        }

        entity.UpdatedAtUtc = entity.UpdatedAtUtc == default ? now : entity.UpdatedAtUtc;
        db.JobSchedules.Add(entity);
        await db.SaveChangesAsync();
        db.Entry(entity).State = EntityState.Detached;
        return entity;
    }

    public async Task<bool> UpdateScheduleAsync(JobScheduleEntity entity)
    {
        var updated = await db.JobSchedules
            .Where(schedule =>
                schedule.Id == entity.Id
                && schedule.ScheduleRevision == entity.ScheduleRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(schedule => schedule.Name, entity.Name)
                .SetProperty(schedule => schedule.JobType, entity.JobType)
                .SetProperty(schedule => schedule.IsEnabled, entity.IsEnabled)
                .SetProperty(schedule => schedule.CronExpression, entity.CronExpression)
                .SetProperty(schedule => schedule.TimeZoneId, entity.TimeZoneId)
                .SetProperty(schedule => schedule.ArgsJson, entity.ArgsJson)
                .SetProperty(schedule => schedule.NextRunUtc, entity.NextRunUtc)
                .SetProperty(schedule => schedule.ScheduleRevision, schedule => schedule.ScheduleRevision + 1)
                .SetProperty(schedule => schedule.UpdatedAtUtc, entity.UpdatedAtUtc));
        return updated == 1;
    }

    public async Task DeleteScheduleAsync(long id)
    {
        var existing = await db.JobSchedules.FirstOrDefaultAsync(s => s.Id == id);
        if (existing is null)
        {
            return;
        }

        db.JobSchedules.Remove(existing);
        await db.SaveChangesAsync();
    }

    public async Task<JobScheduleEntity?> TryAcquireDueScheduleAsync(
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await db.JobSchedules
            .FromSqlInterpolated($"""
                SELECT *
                FROM job_schedules
                WHERE is_enabled = TRUE
                  AND next_run_utc <= {utcNow}
                  AND (lease_expires_utc IS NULL OR lease_expires_utc <= {utcNow})
                ORDER BY next_run_utc, id
                LIMIT 1
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(ct);

        if (schedule is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        ApplyLease(schedule, utcNow, leaseOwner, leaseExpiresUtc);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(schedule).State = EntityState.Detached;
        return schedule;
    }

    public async Task<JobScheduleEntity?> TryAcquireScheduleAsync(
        long id,
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await db.JobSchedules
            .FromSqlInterpolated($"""
                SELECT *
                FROM job_schedules
                WHERE id = {id}
                  AND (lease_expires_utc IS NULL OR lease_expires_utc <= {utcNow})
                LIMIT 1
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(ct);

        if (schedule is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        ApplyLease(schedule, utcNow, leaseOwner, leaseExpiresUtc);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(schedule).State = EntityState.Detached;
        return schedule;
    }

    public async Task<bool> RenewLeaseAsync(
        long id,
        DateTime utcNow,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var leaseExpiresUtc = utcNow.Add(leaseDuration);
        var updated = await db.JobSchedules
            .Where(schedule =>
                schedule.Id == id
                && schedule.LeaseOwner == leaseToken
                && schedule.LeaseExpiresUtc != null
                && schedule.LeaseExpiresUtc > utcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(schedule => schedule.LeaseExpiresUtc, leaseExpiresUtc)
                .SetProperty(schedule => schedule.UpdatedAtUtc, utcNow), ct);
        return updated == 1;
    }

    public async Task<bool> MarkRunStartedAsync(
        long id,
        DateTime startedAtUtc,
        DateTime fenceCheckedAtUtc,
        string leaseToken,
        CancellationToken ct = default)
    {
        var updated = await db.JobSchedules
            .Where(schedule =>
                schedule.Id == id
                && schedule.LeaseOwner == leaseToken
                && schedule.LeaseExpiresUtc != null
                && schedule.LeaseExpiresUtc > fenceCheckedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(schedule => schedule.LastRunStartedUtc, startedAtUtc)
                .SetProperty(schedule => schedule.LastRunStatus, "running")
                .SetProperty(schedule => schedule.LastError, (string?)null)
                .SetProperty(schedule => schedule.UpdatedAtUtc, startedAtUtc), ct);
        return updated == 1;
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
        var updated = nextRunUtc is DateTime nextRun
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE job_schedules
                SET last_run_completed_utc = {completedAtUtc},
                    last_run_status = {status},
                    last_error = {error},
                    next_run_utc = CASE
                        WHEN schedule_revision = {acquiredScheduleRevision} THEN {nextRun}
                        ELSE next_run_utc
                    END,
                    schedule_revision = schedule_revision + 1,
                    lease_acquired_utc = NULL,
                    lease_owner = NULL,
                    lease_expires_utc = NULL,
                    updated_at_utc = {completedAtUtc}
                WHERE id = {id}
                  AND lease_owner = {leaseToken}
                  AND lease_expires_utc IS NOT NULL
                  AND lease_expires_utc > {fenceCheckedAtUtc}
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE job_schedules
                SET last_run_completed_utc = {completedAtUtc},
                    last_run_status = {status},
                    last_error = {error},
                    schedule_revision = schedule_revision + 1,
                    lease_acquired_utc = NULL,
                    lease_owner = NULL,
                    lease_expires_utc = NULL,
                    updated_at_utc = {completedAtUtc}
                WHERE id = {id}
                  AND lease_owner = {leaseToken}
                  AND lease_expires_utc IS NOT NULL
                  AND lease_expires_utc > {fenceCheckedAtUtc}
                """, ct);
        return updated == 1;
    }

    private static void ApplyLease(
        JobScheduleEntity schedule,
        DateTime utcNow,
        string leaseOwner,
        DateTime leaseExpiresUtc)
    {
        schedule.LeaseOwner = leaseOwner;
        schedule.LeaseAcquiredUtc = utcNow;
        schedule.LeaseExpiresUtc = leaseExpiresUtc;
        schedule.UpdatedAtUtc = utcNow;
    }
}
