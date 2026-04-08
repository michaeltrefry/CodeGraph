using System.Text.Json;
using Cronos;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Jobs;

public class JobScheduleService(
    IJobScheduleStore store,
    IJobCommandDispatcher dispatcher,
    ILogger<JobScheduleService> logger) : IJobScheduleService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<IReadOnlyList<JobScheduleResponse>> ListAsync()
    {
        var schedules = await store.ListSchedulesAsync();
        return schedules.Select(MapResponse).ToList();
    }

    public async Task<JobScheduleResponse?> GetAsync(long id)
    {
        var schedule = await store.GetScheduleByIdAsync(id);
        return schedule is null ? null : MapResponse(schedule);
    }

    public async Task<JobScheduleResponse> CreateAsync(CreateJobScheduleRequest request)
    {
        await EnsureUniqueNameAsync(request.Name.Trim(), null);

        var timeZone = ResolveTimeZone(request.TimeZoneId);
        var normalizedArgsJson = dispatcher.NormalizeArgsJson(request.JobType, request.Args);
        var nowUtc = DateTime.UtcNow;
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

        entity = await store.CreateScheduleAsync(entity);
        return MapResponse(entity);
    }

    public async Task<JobScheduleResponse?> UpdateAsync(long id, UpdateJobScheduleRequest request)
    {
        var existing = await store.GetScheduleByIdAsync(id);
        if (existing is null)
            return null;

        await EnsureUniqueNameAsync(request.Name.Trim(), id);

        var timeZone = ResolveTimeZone(request.TimeZoneId);
        existing.Name = Require(request.Name, nameof(request.Name));
        existing.JobType = Require(request.JobType, nameof(request.JobType));
        existing.IsEnabled = request.IsEnabled;
        existing.CronExpression = Require(request.CronExpression, nameof(request.CronExpression));
        existing.TimeZoneId = timeZone.Id;
        existing.ArgsJson = dispatcher.NormalizeArgsJson(request.JobType, request.Args);
        existing.NextRunUtc = ComputeNextRunUtc(existing.CronExpression, timeZone, DateTime.UtcNow);
        existing.UpdatedAtUtc = DateTime.UtcNow;

        await store.UpdateScheduleAsync(existing);
        return MapResponse(existing);
    }

    public async Task<bool> DeleteAsync(long id)
    {
        if (await store.GetScheduleByIdAsync(id) is null)
            return false;

        await store.DeleteScheduleAsync(id);
        return true;
    }

    public async Task<JobScheduleResponse?> SetEnabledAsync(long id, bool isEnabled)
    {
        var schedule = await store.GetScheduleByIdAsync(id);
        if (schedule is null)
            return null;

        schedule.IsEnabled = isEnabled;
        schedule.UpdatedAtUtc = DateTime.UtcNow;
        if (isEnabled)
        {
            schedule.NextRunUtc = ComputeNextRunUtc(
                schedule.CronExpression,
                ResolveTimeZone(schedule.TimeZoneId),
                DateTime.UtcNow);
        }

        await store.UpdateScheduleAsync(schedule);
        return MapResponse(schedule);
    }

    public async Task<JobExecutionResponse?> RunNowAsync(long id, CancellationToken ct = default)
    {
        var schedule = await store.GetScheduleByIdAsync(id);
        if (schedule is null)
            return null;

        var utcNow = DateTime.UtcNow;
        var acquired = await store.TryAcquireScheduleAsync(id, utcNow, _leaseOwner, LeaseDuration, ct);
        if (acquired is null)
            throw new InvalidOperationException("Schedule is already running.");

        return await ExecuteScheduleAsync(acquired, isManual: true, ct);
    }

    public async Task<bool> TryRunNextDueScheduleAsync(CancellationToken ct = default)
    {
        var acquired = await store.TryAcquireDueScheduleAsync(DateTime.UtcNow, _leaseOwner, LeaseDuration, ct);
        if (acquired is null)
            return false;

        await ExecuteScheduleAsync(acquired, isManual: false, ct);
        return true;
    }

    private async Task<JobExecutionResponse> ExecuteScheduleAsync(JobScheduleEntity schedule, bool isManual, CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        await store.MarkRunStartedAsync(schedule.Id, startedAtUtc, _leaseOwner, ct);

        JobExecutionResult result;
        try
        {
            result = await dispatcher.ExecuteAsync(schedule.JobType, schedule.ArgsJson, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {ScheduleId}:{JobType} failed", schedule.Id, schedule.JobType);
            var completedAtUtc = DateTime.UtcNow;
            await store.MarkRunCompletedAsync(
                schedule.Id,
                completedAtUtc,
                GetNextRunAfterCompletion(schedule, completedAtUtc, isManual),
                "failed",
                ex.Message,
                _leaseOwner,
                ct);

            return new JobExecutionResponse(false, ex.Message, startedAtUtc, completedAtUtc);
        }

        await store.MarkRunCompletedAsync(
            schedule.Id,
            result.CompletedAtUtc,
            GetNextRunAfterCompletion(schedule, result.CompletedAtUtc, isManual),
            result.Success ? "succeeded" : "failed",
            result.Success ? null : result.Message,
            _leaseOwner,
            ct);

        return new JobExecutionResponse(result.Success, result.Message, result.StartedAtUtc, result.CompletedAtUtc);
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

        var existing = await store.GetScheduleByNameAsync(name);
        if (existing is not null && existing.Id != currentId)
            throw new InvalidOperationException($"A schedule named '{name}' already exists.");
    }

    private static string Require(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
        return value.Trim();
    }

    private static JobScheduleResponse MapResponse(JobScheduleEntity entity)
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
            entity.LeaseExpiresUtc.HasValue && entity.LeaseExpiresUtc > DateTime.UtcNow);
    }
}
