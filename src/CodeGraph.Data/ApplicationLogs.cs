namespace CodeGraph.Data;

public sealed class ApplicationLogEntryEntity
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? PropertiesJson { get; set; }
}

public sealed record ApplicationLogQuery(
    int Page,
    int PageSize,
    string? Level,
    DateTime? StartUtc,
    DateTime? EndUtc,
    string? Search);

public sealed record ApplicationLogPage(
    IReadOnlyList<ApplicationLogEntryEntity> Entries,
    long TotalCount);

public interface IApplicationLogStore
{
    Task WriteBatchAsync(
        IReadOnlyList<ApplicationLogEntryEntity> entries,
        CancellationToken cancellationToken = default);

    Task<ApplicationLogPage> QueryAsync(
        ApplicationLogQuery query,
        CancellationToken cancellationToken = default);

    Task<int> DeleteBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}
