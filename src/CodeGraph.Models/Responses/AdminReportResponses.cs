namespace CodeGraph.Models.Responses;

public record AdminReportRangeResponse(
    DateTime Start,
    DateTime End);

public record AdminReportAppliedFiltersResponse(
    string? User,
    string? Provider,
    string? Model,
    string? Tool);

public record AdminSummaryCardResponse(
    string Key,
    string Label,
    long Value);

public record AdminSeriesPointResponse(
    DateTime BucketStart,
    long Value);

public record AdminReportSeriesResponse(
    string Key,
    string Label,
    IReadOnlyList<AdminSeriesPointResponse> Points);

public record AdminBreakdownItemResponse(
    string Dimension,
    string Key,
    string Label,
    long Value);

public record AdminReportResponse(
    AdminReportRangeResponse Range,
    string Interval,
    IReadOnlyList<AdminSummaryCardResponse> Totals,
    IReadOnlyList<AdminReportSeriesResponse> Series,
    IReadOnlyList<AdminBreakdownItemResponse> Breakdowns,
    AdminReportAppliedFiltersResponse AppliedFilters);

public record AdminReportFiltersResponse(
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Tools);
