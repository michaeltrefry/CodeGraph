namespace CodeGraph.Data;

public interface IJobScheduleStore
{
    Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync();
    Task<JobScheduleEntity?> GetScheduleByIdAsync(long id);
    Task<JobScheduleEntity?> GetScheduleByNameAsync(string name);
    Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity);
    Task<bool> UpdateScheduleAsync(JobScheduleEntity entity);
    Task DeleteScheduleAsync(long id);

    Task<JobScheduleEntity?> TryAcquireDueScheduleAsync(
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<JobScheduleEntity?> TryAcquireScheduleAsync(
        long id,
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> RenewLeaseAsync(
        long id,
        DateTime utcNow,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> MarkRunStartedAsync(
        long id,
        DateTime startedAtUtc,
        DateTime fenceCheckedAtUtc,
        string leaseToken,
        CancellationToken ct = default);

    Task<bool> MarkRunCompletedAsync(
        long id,
        DateTime completedAtUtc,
        DateTime? nextRunUtc,
        string status,
        string? error,
        DateTime fenceCheckedAtUtc,
        long acquiredScheduleRevision,
        string leaseToken,
        CancellationToken ct = default);
}
