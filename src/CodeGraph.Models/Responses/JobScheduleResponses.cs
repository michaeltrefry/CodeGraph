using System.Text.Json;

namespace CodeGraph.Models.Responses;

public record JobScheduleResponse(
    long Id,
    string Name,
    string JobType,
    bool IsEnabled,
    string CronExpression,
    string TimeZoneId,
    JsonElement Args,
    DateTime NextRunUtc,
    DateTime? LastRunStartedUtc,
    DateTime? LastRunCompletedUtc,
    string? LastRunStatus,
    string? LastError,
    bool IsRunning);

public record JobExecutionResponse(
    bool Success,
    string Message,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);
