namespace CodeGraph.Data;

public interface IJobScheduleStore
{
    Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync();
    Task<JobScheduleEntity?> GetScheduleByIdAsync(long id);
    Task<JobScheduleEntity?> GetScheduleByNameAsync(string name);
    Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity);
    Task UpdateScheduleAsync(JobScheduleEntity entity);
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

    Task MarkRunStartedAsync(long id, DateTime startedAtUtc, string leaseOwner, CancellationToken ct = default);

    Task MarkRunCompletedAsync(
        long id,
        DateTime completedAtUtc,
        DateTime? nextRunUtc,
        string status,
        string? error,
        string leaseOwner,
        CancellationToken ct = default);
}
