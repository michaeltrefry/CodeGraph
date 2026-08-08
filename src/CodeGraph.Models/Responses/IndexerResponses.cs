namespace CodeGraph.Models.Responses;

public sealed record IndexerAcceptedResponse(
    string Status,
    string? Message = null,
    long? RunId = null,
    string? RunStatusUrl = null,
    string? SubmissionKey = null,
    bool Duplicate = false);

public sealed record IndexerRunResponse(
    long Id,
    string Operation,
    string Status,
    string? RequestedByUsername,
    string? Target,
    string? Message,
    string? ErrorCode,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int AttemptCount = 0,
    DateTime? HeartbeatAt = null,
    DateTime? LeaseExpiresAt = null,
    DateTime? CancelRequestedAt = null,
    DateTime? NextAttemptAt = null,
    bool RetrySafe = false,
    string? SubmissionKey = null);
