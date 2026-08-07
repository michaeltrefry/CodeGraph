namespace CodeGraph.Models.Responses;

public sealed record ApplicationLogEntryResponse(
    long Id,
    DateTime OccurredAtUtc,
    string Level,
    string Source,
    string Category,
    int EventId,
    string Message,
    string? Exception,
    string? TraceId,
    string? SpanId,
    string? PropertiesJson);

public sealed record ApplicationLogPageResponse(
    IReadOnlyList<ApplicationLogEntryResponse> Entries,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);
