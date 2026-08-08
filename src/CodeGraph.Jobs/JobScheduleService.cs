using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cronos;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Jobs;

public class JobScheduleService : IJobScheduleService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromMinutes(5);
    private const int ScheduleUpdateAttempts = 5;
    private readonly IJobScheduleStore _store;
    private readonly IJobCommandDispatcher _dispatcher;
    private readonly ILogger<JobScheduleService> _logger;
    private readonly IJobScheduleClock _clock;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public JobScheduleService(
        IJobScheduleStore store,
        IJobCommandDispatcher dispatcher,
        ILogger<JobScheduleService> logger,
        IJobScheduleClock? clock = null)
    {
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;
        _clock = clock ?? new SystemJobScheduleClock();
    }

    public async Task<IReadOnlyList<JobScheduleResponse>> ListAsync()
    {
        var schedules = await _store.ListSchedulesAsync();
        return schedules.Select(MapResponse).ToList();
    }

    public async Task<JobScheduleResponse?> GetAsync(long id)
    {
        var schedule = await _store.GetScheduleByIdAsync(id);
        return schedule is null ? null : MapResponse(schedule);
    }

    public async Task<JobScheduleResponse> CreateAsync(CreateJobScheduleRequest request)
    {
        await EnsureUniqueNameAsync(request.Name.Trim(), null);

        var timeZone = ResolveTimeZone(request.TimeZoneId);
        var normalizedArgsJson = _dispatcher.NormalizeArgsJson(request.JobType, request.Args);
        var nowUtc = _clock.UtcNow;
        var entity = new JobScheduleEntity
        {
            Name = Require(request.Name, nameof(request.Name)),
            JobType = Require(request.JobType, nameof(request.JobType)),
            IsEnabled = request.IsEnabled,
            CronExpression = Require(request.CronExpression, nameof(request.CronExpression)),
            TimeZoneId = timeZone.Id,
            ArgsJson = normalizedArgsJson,
            NextRunUtc = ComputeNextRunUtc(request.CronExpression, timeZone, nowUtc),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        entity = await _store.CreateScheduleAsync(entity);
        return MapResponse(entity);
    }

    public async Task<JobScheduleResponse?> UpdateAsync(long id, UpdateJobScheduleRequest request)
    {
        if (await _store.GetScheduleByIdAsync(id) is null)
            return null;

        await EnsureUniqueNameAsync(request.Name.Trim(), id);
        var timeZone = ResolveTimeZone(request.TimeZoneId);
        var name = Require(request.Name, nameof(request.Name));
        var jobType = Require(request.JobType, nameof(request.JobType));
        var cronExpression = Require(request.CronExpression, nameof(request.CronExpression));
        var argsJson = _dispatcher.NormalizeArgsJson(request.JobType, request.Args);

        for (var attempt = 0; attempt < ScheduleUpdateAttempts; attempt++)
        {
            var existing = await _store.GetScheduleByIdAsync(id);
            if (existing is null)
                return null;

            var nowUtc = _clock.UtcNow;
            existing.Name = name;
            existing.JobType = jobType;
            existing.IsEnabled = request.IsEnabled;
            existing.CronExpression = cronExpression;
            existing.TimeZoneId = timeZone.Id;
            existing.ArgsJson = argsJson;
            existing.NextRunUtc = ComputeNextRunUtc(cronExpression, timeZone, nowUtc);
            existing.UpdatedAtUtc = nowUtc;

            if (await _store.UpdateScheduleAsync(existing))
            {
                var updated = await _store.GetScheduleByIdAsync(id);
                return MapResponse(updated ?? existing);
            }
        }

        throw new InvalidOperationException("Schedule changed concurrently; retry the update.");
    }

    public async Task<bool> DeleteAsync(long id)
    {
        if (await _store.GetScheduleByIdAsync(id) is null)
            return false;

        await _store.DeleteScheduleAsync(id);
        return true;
    }

    public async Task<JobScheduleResponse?> SetEnabledAsync(long id, bool isEnabled)
    {
        for (var attempt = 0; attempt < ScheduleUpdateAttempts; attempt++)
        {
            var schedule = await _store.GetScheduleByIdAsync(id);
            if (schedule is null)
                return null;

            var nowUtc = _clock.UtcNow;
            schedule.IsEnabled = isEnabled;
            schedule.UpdatedAtUtc = nowUtc;
            if (isEnabled)
            {
                schedule.NextRunUtc = ComputeNextRunUtc(
                    schedule.CronExpression,
                    ResolveTimeZone(schedule.TimeZoneId),
                    nowUtc);
            }

            if (await _store.UpdateScheduleAsync(schedule))
            {
                var updated = await _store.GetScheduleByIdAsync(id);
                return MapResponse(updated ?? schedule);
            }
        }

        throw new InvalidOperationException("Schedule changed concurrently; retry the update.");
    }

    public async Task<JobExecutionResponse?> RunNowAsync(long id, CancellationToken ct = default)
    {
        var schedule = await _store.GetScheduleByIdAsync(id);
        if (schedule is null)
            return null;

        var utcNow = _clock.UtcNow;
        var leaseToken = CreateLeaseToken();
        var acquired = await _store.TryAcquireScheduleAsync(id, utcNow, leaseToken, LeaseDuration, ct);
        if (acquired is null)
            throw new InvalidOperationException("Schedule is already running.");

        return await ExecuteScheduleAsync(acquired, leaseToken, isManual: true, ct);
    }

    public async Task<bool> TryRunNextDueScheduleAsync(CancellationToken ct = default)
    {
        var leaseToken = CreateLeaseToken();
        var acquired = await _store.TryAcquireDueScheduleAsync(
            _clock.UtcNow, leaseToken, LeaseDuration, ct);
        if (acquired is null)
            return false;

        await ExecuteScheduleAsync(acquired, leaseToken, isManual: false, ct);
        return true;
    }

    private async Task<JobExecutionResponse> ExecuteScheduleAsync(
        JobScheduleEntity schedule,
        string leaseToken,
        bool isManual,
        CancellationToken ct)
    {
        // A failed response or expired lease can cause the same logical schedule
        // execution to be retried. Reuse its durable identity until one attempt is
        // acknowledged as successful, then rotate it for the next occurrence.
        var startedAtUtc = NormalizeToDatabasePrecision(
            schedule.LastRunStartedUtc.HasValue && schedule.LastRunStatus is "running" or "failed"
                ? schedule.LastRunStartedUtc.Value
                : _clock.UtcNow);
        var executionKey = CreateExecutionKey(schedule, startedAtUtc);
        if (!await _store.MarkRunStartedAsync(
                schedule.Id, startedAtUtc, _clock.UtcNow, leaseToken, ct))
            throw new JobScheduleLeaseLostException(schedule.Id);

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalTask = RenewLeaseUntilStoppedAsync(
            schedule.Id, leaseToken, executionCts, leaseLost);

        JobExecutionResult result;
        try
        {
            result = await _dispatcher.ExecuteAsync(
                schedule.JobType, schedule.ArgsJson, executionKey, executionCts.Token);
        }
        catch (Exception ex)
        {
            executionCts.Cancel();
            await AwaitRenewalShutdownAsync(renewalTask);
            if (leaseLost.Task.IsCompleted)
                throw new JobScheduleLeaseLostException(schedule.Id);

            _logger.LogError(ex, "Scheduled job {ScheduleId}:{JobType} failed", schedule.Id, schedule.JobType);
            var completedAtUtc = _clock.UtcNow;
            var completed = await _store.MarkRunCompletedAsync(
                schedule.Id,
                completedAtUtc,
                GetNextRunAfterCompletion(schedule, completedAtUtc, isManual),
                "failed",
                ex.Message,
                _clock.UtcNow,
                schedule.ScheduleRevision,
                leaseToken,
                CancellationToken.None);
            if (!completed)
                throw new JobScheduleLeaseLostException(schedule.Id);

            return new JobExecutionResponse(false, ex.Message, startedAtUtc, completedAtUtc);
        }

        executionCts.Cancel();
        await AwaitRenewalShutdownAsync(renewalTask);
        if (leaseLost.Task.IsCompleted)
            throw new JobScheduleLeaseLostException(schedule.Id);

        var completionRecorded = await _store.MarkRunCompletedAsync(
            schedule.Id,
            result.CompletedAtUtc,
            GetNextRunAfterCompletion(schedule, result.CompletedAtUtc, isManual),
            result.Success ? "succeeded" : "failed",
            result.Success ? null : result.Message,
            _clock.UtcNow,
            schedule.ScheduleRevision,
            leaseToken,
            CancellationToken.None);
        if (!completionRecorded)
            throw new JobScheduleLeaseLostException(schedule.Id);

        return new JobExecutionResponse(result.Success, result.Message, result.StartedAtUtc, result.CompletedAtUtc);
    }

    private async Task RenewLeaseUntilStoppedAsync(
        long scheduleId,
        string leaseToken,
        CancellationTokenSource executionCts,
        TaskCompletionSource leaseLost)
    {
        try
        {
            while (!executionCts.IsCancellationRequested)
            {
                await _clock.DelayAsync(LeaseRenewalInterval, executionCts.Token);
                var renewed = await _store.RenewLeaseAsync(
                    scheduleId,
                    _clock.UtcNow,
                    leaseToken,
                    LeaseDuration,
                    executionCts.Token);
                if (renewed)
                    continue;

                leaseLost.TrySetResult();
                executionCts.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lease renewal failed for schedule {ScheduleId}", scheduleId);
            leaseLost.TrySetResult();
            executionCts.Cancel();
        }
    }

    private static async Task AwaitRenewalShutdownAsync(Task renewalTask)
    {
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string CreateLeaseToken() => $"{_workerId}:{Guid.NewGuid():N}";

    private static string CreateExecutionKey(JobScheduleEntity schedule, DateTime startedAtUtc)
    {
        var identity = $"{schedule.Id}\n{startedAtUtc.Ticks}\n{schedule.JobType}\n{schedule.ArgsJson}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"job:{schedule.Id}:{hash}";
    }

    private static DateTime NormalizeToDatabasePrecision(DateTime value)
    {
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return new DateTime(value.Ticks - (value.Ticks % ticksPerMicrosecond), value.Kind);
    }
    private DateTime? GetNextRunAfterCompletion(JobScheduleEntity schedule, DateTime completedAtUtc, bool isManual)
    {
        if (!schedule.IsEnabled)
            return schedule.NextRunUtc;

        if (isManual && schedule.NextRunUtc > completedAtUtc)
            return schedule.NextRunUtc;

        return ComputeNextRunUtc(schedule.CronExpression, ResolveTimeZone(schedule.TimeZoneId), completedAtUtc);
    }

    private static DateTime ComputeNextRunUtc(string cronExpression, TimeZoneInfo timeZone, DateTime fromUtc)
    {
        var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
        var next = cron.GetNextOccurrence(fromUtc, timeZone, inclusive: false);
        if (next is null)
            throw new InvalidOperationException("Cron expression does not produce a future occurrence.");

        return DateTime.SpecifyKind(next.Value, DateTimeKind.Utc);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var normalized = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(normalized);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException($"Unknown time zone '{normalized}'.", ex);
        }
    }

    private async Task EnsureUniqueNameAsync(string name, long? currentId)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");

        var existing = await _store.GetScheduleByNameAsync(name);
        if (existing is not null && existing.Id != currentId)
            throw new InvalidOperationException($"A schedule named '{name}' already exists.");
    }

    private static string Require(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
        return value.Trim();
    }

    private JobScheduleResponse MapResponse(JobScheduleEntity entity)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(entity.ArgsJson) ? "{}" : entity.ArgsJson);
        return new JobScheduleResponse(
            entity.Id,
            entity.Name,
            entity.JobType,
            entity.IsEnabled,
            entity.CronExpression,
            entity.TimeZoneId,
            document.RootElement.Clone(),
            entity.NextRunUtc,
            entity.LastRunStartedUtc,
            entity.LastRunCompletedUtc,
            entity.LastRunStatus,
            entity.LastError,
            entity.LeaseExpiresUtc.HasValue && entity.LeaseExpiresUtc > _clock.UtcNow);
    }
}
