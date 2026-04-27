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
        return entity;
    }

    public async Task UpdateScheduleAsync(JobScheduleEntity entity)
    {
        var existing = await db.JobSchedules.FirstOrDefaultAsync(s => s.Id == entity.Id);
        if (existing is null)
        {
            return;
        }

        existing.Name = entity.Name;
        existing.JobType = entity.JobType;
        existing.IsEnabled = entity.IsEnabled;
        existing.CronExpression = entity.CronExpression;
        existing.TimeZoneId = entity.TimeZoneId;
        existing.ArgsJson = entity.ArgsJson;
        existing.NextRunUtc = entity.NextRunUtc;
        existing.LastRunStartedUtc = entity.LastRunStartedUtc;
        existing.LastRunCompletedUtc = entity.LastRunCompletedUtc;
        existing.LastRunStatus = entity.LastRunStatus;
        existing.LastError = entity.LastError;
        existing.LeaseAcquiredUtc = entity.LeaseAcquiredUtc;
        existing.LeaseOwner = entity.LeaseOwner;
        existing.LeaseExpiresUtc = entity.LeaseExpiresUtc;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync();
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

    public async Task MarkRunStartedAsync(long id, DateTime startedAtUtc, string leaseOwner, CancellationToken ct = default)
    {
        var schedule = await db.JobSchedules.FirstOrDefaultAsync(
            s => s.Id == id && s.LeaseOwner == leaseOwner,
            ct);

        if (schedule is null)
        {
            return;
        }

        schedule.LastRunStartedUtc = startedAtUtc;
        schedule.LastRunStatus = "running";
        schedule.LastError = null;
        schedule.UpdatedAtUtc = startedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkRunCompletedAsync(
        long id,
        DateTime completedAtUtc,
        DateTime? nextRunUtc,
        string status,
        string? error,
        string leaseOwner,
        CancellationToken ct = default)
    {
        var schedule = await db.JobSchedules.FirstOrDefaultAsync(
            s => s.Id == id && s.LeaseOwner == leaseOwner,
            ct);

        if (schedule is null)
        {
            return;
        }

        schedule.LastRunCompletedUtc = completedAtUtc;
        schedule.LastRunStatus = status;
        schedule.LastError = error;
        if (nextRunUtc is not null)
        {
            schedule.NextRunUtc = nextRunUtc.Value;
        }

        schedule.LeaseAcquiredUtc = null;
        schedule.LeaseOwner = null;
        schedule.LeaseExpiresUtc = null;
        schedule.UpdatedAtUtc = completedAtUtc;
        await db.SaveChangesAsync(ct);
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
