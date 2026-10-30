namespace CodeGraph.Models.Responses;

public sealed record JobQueueEntry(string Kind, long Id, string Operation, string? Target, string Status,
    string? RequestedByUsername, string? Message, string? Error, DateTime CreatedAt, DateTime? StartedAt,
    DateTime? NextAttemptAt, int AttemptCount, bool Cancelable);

public sealed record JobQueueResponse(IReadOnlyList<JobQueueEntry> Items, int Running, int Queued, int Failed,
    DateTime GeneratedAt);
