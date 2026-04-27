namespace CodeGraph.Models.Responses;

public sealed record IndexerAcceptedResponse(
    string Status,
    string? Message = null,
    long? RunId = null,
    string? RunStatusUrl = null);

public sealed record IndexerRunResponse(
    long Id,
    string Operation,
    string Status,
    string? RequestedByUsername,
    string? Target,
    string? Message,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

