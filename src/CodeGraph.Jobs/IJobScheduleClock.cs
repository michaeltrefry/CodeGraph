namespace CodeGraph.Jobs;

public interface IJobScheduleClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemJobScheduleClock : IJobScheduleClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, ct);
}

public sealed class JobScheduleLeaseLostException(long scheduleId)
    : InvalidOperationException($"Lease ownership was lost for schedule {scheduleId}.")
{
    public long ScheduleId { get; } = scheduleId;
}
