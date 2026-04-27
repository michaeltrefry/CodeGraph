using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public class AdminReportsService(IAdminReportsStore store, TimeProvider? timeProvider = null) : IAdminReportsService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminReportResponse> GetAssistantUsageAsync(
        AdminReportQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = CreateContext(request, supportsProviderModel: true, supportsTool: false);
        var usageRows = await store.GetLlmUsageAsync(
            context.Start,
            context.End,
            path: "Assistant",
            username: context.AppliedFilters.User,
            provider: context.AppliedFilters.Provider,
            model: context.AppliedFilters.Model,
            ct: cancellationToken);
        var runRows = await store.GetAssistantRunsAsync(
            context.Start,
            context.End,
            username: context.AppliedFilters.User,
            provider: context.AppliedFilters.Provider,
            model: context.AppliedFilters.Model,
            ct: cancellationToken);

        var buckets = GetBuckets(context);
        var totals = new List<AdminSummaryCardResponse>
        {
            new("totalTokens", "Total Tokens", usageRows.Sum(row => (long)row.TotalTokens)),
            new("inputTokens", "Input Tokens", usageRows.Sum(row => (long)row.InputTokens)),
            new("outputTokens", "Output Tokens", usageRows.Sum(row => (long)row.OutputTokens)),
            new("runCount", "Run Count", runRows.Count),
            new("uniqueChats", "Unique Chats", runRows.Select(row => $"{row.Username}:{row.ChatId}").Distinct(StringComparer.Ordinal).LongCount())
        };

        var series = new List<AdminReportSeriesResponse>
        {
            BuildSeries(buckets, "totalTokens", "Total Tokens", usageRows, context.Interval, row => row.CreatedAt, row => row.TotalTokens),
            BuildSeries(buckets, "inputTokens", "Input Tokens", usageRows, context.Interval, row => row.CreatedAt, row => row.InputTokens),
            BuildSeries(buckets, "outputTokens", "Output Tokens", usageRows, context.Interval, row => row.CreatedAt, row => row.OutputTokens),
            BuildCountSeries(buckets, "runCount", "Run Count", runRows, context.Interval, row => row.CreatedAt),
            BuildUniqueChatSeries(buckets, "uniqueChats", "Unique Chats", runRows, context.Interval)
        };
        series.AddRange(BuildBreakdownValueSeries(buckets, "user", usageRows, context.Interval, row => row.CreatedAt, row => row.Username, row => row.TotalTokens));
        series.AddRange(BuildBreakdownValueSeries(buckets, "provider", usageRows, context.Interval, row => row.CreatedAt, row => row.Provider, row => row.TotalTokens));
        series.AddRange(BuildBreakdownValueSeries(buckets, "model", usageRows, context.Interval, row => row.CreatedAt, row => row.Model, row => row.TotalTokens));
        series.AddRange(BuildBreakdownCountSeries(buckets, "status", runRows, context.Interval, row => row.CreatedAt, row => row.Status));

        var breakdowns = new List<AdminBreakdownItemResponse>();
        breakdowns.AddRange(BuildBreakdowns("user", usageRows.GroupBy(row => row.Username).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));
        breakdowns.AddRange(BuildBreakdowns("provider", usageRows.GroupBy(row => row.Provider).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));
        breakdowns.AddRange(BuildBreakdowns("model", usageRows.GroupBy(row => row.Model).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));

        return CreateResponse(context, totals, series, breakdowns);
    }

    public async Task<AdminReportResponse> GetAssistantActivityAsync(
        AdminReportQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = CreateContext(request, supportsProviderModel: true, supportsTool: false);
        var rows = await store.GetAssistantRunsAsync(
            context.Start,
            context.End,
            username: context.AppliedFilters.User,
            provider: context.AppliedFilters.Provider,
            model: context.AppliedFilters.Model,
            ct: cancellationToken);

        var buckets = GetBuckets(context);
        var totals = new List<AdminSummaryCardResponse>
        {
            new("runCount", "Run Count", rows.Count),
            new("uniqueChats", "Unique Chats", rows.Select(row => $"{row.Username}:{row.ChatId}").Distinct(StringComparer.Ordinal).LongCount()),
            new("completedRuns", "Completed Runs", rows.LongCount(row => IsStatus(row.Status, "completed"))),
            new("failedRuns", "Failed Runs", rows.LongCount(row => IsStatus(row.Status, "failed"))),
            new("cancelledRuns", "Cancelled Runs", rows.LongCount(row => IsStatus(row.Status, "cancelled")))
        };

        var series = new List<AdminReportSeriesResponse>
        {
            BuildCountSeries(buckets, "runCount", "Run Count", rows, context.Interval, row => row.CreatedAt),
            BuildUniqueChatSeries(buckets, "uniqueChats", "Unique Chats", rows, context.Interval),
            BuildFilteredCountSeries(buckets, "completedRuns", "Completed Runs", rows, context.Interval, row => row.CreatedAt, row => IsStatus(row.Status, "completed")),
            BuildFilteredCountSeries(buckets, "failedRuns", "Failed Runs", rows, context.Interval, row => row.CreatedAt, row => IsStatus(row.Status, "failed")),
            BuildFilteredCountSeries(buckets, "cancelledRuns", "Cancelled Runs", rows, context.Interval, row => row.CreatedAt, row => IsStatus(row.Status, "cancelled"))
        };
        series.AddRange(BuildBreakdownCountSeries(buckets, "status", rows, context.Interval, row => row.CreatedAt, row => row.Status));
        series.AddRange(BuildBreakdownCountSeries(buckets, "user", rows, context.Interval, row => row.CreatedAt, row => row.Username));
        series.AddRange(BuildBreakdownCountSeries(buckets, "provider", rows, context.Interval, row => row.CreatedAt, row => NormalizeBreakdownKey(row.ProviderUsed)));
        series.AddRange(BuildBreakdownCountSeries(buckets, "model", rows, context.Interval, row => row.CreatedAt, row => NormalizeBreakdownKey(row.ModelUsed)));

        var breakdowns = new List<AdminBreakdownItemResponse>();
        breakdowns.AddRange(BuildBreakdowns("status", rows.GroupBy(row => row.Status).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));
        breakdowns.AddRange(BuildBreakdowns("user", rows.GroupBy(row => row.Username).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));
        breakdowns.AddRange(BuildBreakdowns("provider", rows.GroupBy(row => NormalizeBreakdownKey(row.ProviderUsed)).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));
        breakdowns.AddRange(BuildBreakdowns("model", rows.GroupBy(row => NormalizeBreakdownKey(row.ModelUsed)).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));

        return CreateResponse(context, totals, series, breakdowns);
    }

    public async Task<AdminReportResponse> GetMcpUsageAsync(
        AdminReportQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = CreateContext(request, supportsProviderModel: false, supportsTool: true);
        var rows = await store.GetMcpToolInvocationsAsync(
            context.Start,
            context.End,
            username: context.AppliedFilters.User,
            tool: context.AppliedFilters.Tool,
            ct: cancellationToken);

        var buckets = GetBuckets(context);
        var totals = new List<AdminSummaryCardResponse>
        {
            new("callCount", "Call Count", rows.Count),
            new("successfulCalls", "Successful Calls", rows.LongCount(row => row.Success)),
            new("failedCalls", "Failed Calls", rows.LongCount(row => !row.Success)),
            new("uniqueUsers", "Unique Users", rows.Where(row => !string.IsNullOrWhiteSpace(row.Username)).Select(row => row.Username!).Distinct(StringComparer.Ordinal).LongCount()),
            new("averageDurationMs", "Avg Duration (ms)", rows.Count == 0 ? 0 : (long)Math.Round(rows.Average(row => row.DurationMs)))
        };

        var series = new List<AdminReportSeriesResponse>
        {
            BuildCountSeries(buckets, "callCount", "Call Count", rows, context.Interval, row => row.CreatedAt),
            BuildFilteredCountSeries(buckets, "successfulCalls", "Successful Calls", rows, context.Interval, row => row.CreatedAt, row => row.Success),
            BuildFilteredCountSeries(buckets, "failedCalls", "Failed Calls", rows, context.Interval, row => row.CreatedAt, row => !row.Success),
            BuildAverageSeries(buckets, "averageDurationMs", "Avg Duration (ms)", rows, context.Interval, row => row.CreatedAt, row => row.DurationMs)
        };
        series.AddRange(BuildBreakdownCountSeries(buckets, "tool", rows, context.Interval, row => row.CreatedAt, row => row.ToolName));
        series.AddRange(BuildBreakdownCountSeries(buckets, "user", rows, context.Interval, row => row.CreatedAt, row => NormalizeBreakdownKey(row.Username)));
        series.AddRange(BuildBreakdownCountSeries(buckets, "status", rows, context.Interval, row => row.CreatedAt, row => row.Success ? "success" : "failure"));

        var breakdowns = new List<AdminBreakdownItemResponse>();
        breakdowns.AddRange(BuildBreakdowns("tool", rows.GroupBy(row => row.ToolName).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));
        breakdowns.AddRange(BuildBreakdowns("user", rows.GroupBy(row => NormalizeBreakdownKey(row.Username)).Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));
        breakdowns.AddRange(BuildBreakdowns("status", rows.GroupBy(row => row.Success ? "success" : "failure").Select(group => new KeyValuePair<string, long>(group.Key, group.LongCount()))));

        return CreateResponse(context, totals, series, breakdowns);
    }

    public Task<AdminReportResponse> GetCodeReviewUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default) =>
        BuildUsageReportAsync("CodeReview", "requestCount", "Request Count", request, cancellationToken);

    public Task<AdminReportResponse> GetRepositoryAnalysisUsageAsync(AdminReportQueryRequest request, CancellationToken cancellationToken = default) =>
        BuildUsageReportAsync("Analysis", "requestCount", "Request Count", request, cancellationToken);

    public async Task<AdminReportFiltersResponse> GetFiltersAsync(
        AdminReportQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = CreateContext(request, supportsProviderModel: true, supportsTool: true);
        var llmUsage = await store.GetLlmUsageAsync(context.Start, context.End, ct: cancellationToken);
        var runs = await store.GetAssistantRunsAsync(context.Start, context.End, ct: cancellationToken);
        var invocations = await store.GetMcpToolInvocationsAsync(context.Start, context.End, ct: cancellationToken);

        var users = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in llmUsage.Select(row => row.Username))
            AddIfPresent(users, value);
        foreach (var value in runs.Select(row => row.Username))
            AddIfPresent(users, value);
        foreach (var value in invocations.Select(row => row.Username))
            AddIfPresent(users, value);

        var providers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in llmUsage.Select(row => row.Provider))
            AddIfPresent(providers, value);
        foreach (var value in runs.Select(row => row.ProviderUsed))
            AddIfPresent(providers, value);

        var models = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in llmUsage.Select(row => row.Model))
            AddIfPresent(models, value);
        foreach (var value in runs.Select(row => row.ModelUsed))
            AddIfPresent(models, value);

        var tools = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in invocations.Select(row => row.ToolName))
            AddIfPresent(tools, value);

        return new AdminReportFiltersResponse(users.ToList(), providers.ToList(), models.ToList(), tools.ToList());
    }

    private async Task<AdminReportResponse> BuildUsageReportAsync(
        string path,
        string countKey,
        string countLabel,
        AdminReportQueryRequest request,
        CancellationToken cancellationToken)
    {
        var context = CreateContext(request, supportsProviderModel: true, supportsTool: false);
        var rows = await store.GetLlmUsageAsync(
            context.Start,
            context.End,
            path,
            context.AppliedFilters.User,
            context.AppliedFilters.Provider,
            context.AppliedFilters.Model,
            cancellationToken);

        var buckets = GetBuckets(context);
        var totals = new List<AdminSummaryCardResponse>
        {
            new("totalTokens", "Total Tokens", rows.Sum(row => (long)row.TotalTokens)),
            new("inputTokens", "Input Tokens", rows.Sum(row => (long)row.InputTokens)),
            new("outputTokens", "Output Tokens", rows.Sum(row => (long)row.OutputTokens)),
            new(countKey, countLabel, rows.Count),
            new("uniqueUsers", "Unique Users", rows.Select(row => row.Username).Distinct(StringComparer.Ordinal).LongCount())
        };

        var series = new List<AdminReportSeriesResponse>
        {
            BuildSeries(buckets, "totalTokens", "Total Tokens", rows, context.Interval, row => row.CreatedAt, row => row.TotalTokens),
            BuildSeries(buckets, "inputTokens", "Input Tokens", rows, context.Interval, row => row.CreatedAt, row => row.InputTokens),
            BuildSeries(buckets, "outputTokens", "Output Tokens", rows, context.Interval, row => row.CreatedAt, row => row.OutputTokens),
            BuildCountSeries(buckets, countKey, countLabel, rows, context.Interval, row => row.CreatedAt)
        };
        series.AddRange(BuildBreakdownValueSeries(buckets, "user", rows, context.Interval, row => row.CreatedAt, row => row.Username, row => row.TotalTokens));
        series.AddRange(BuildBreakdownValueSeries(buckets, "provider", rows, context.Interval, row => row.CreatedAt, row => row.Provider, row => row.TotalTokens));
        series.AddRange(BuildBreakdownValueSeries(buckets, "model", rows, context.Interval, row => row.CreatedAt, row => row.Model, row => row.TotalTokens));

        var breakdowns = new List<AdminBreakdownItemResponse>();
        breakdowns.AddRange(BuildBreakdowns("user", rows.GroupBy(row => row.Username).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));
        breakdowns.AddRange(BuildBreakdowns("provider", rows.GroupBy(row => row.Provider).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));
        breakdowns.AddRange(BuildBreakdowns("model", rows.GroupBy(row => row.Model).Select(group => new KeyValuePair<string, long>(group.Key, group.Sum(row => (long)row.TotalTokens)))));

        return CreateResponse(context, totals, series, breakdowns);
    }

    private AdminReportContext CreateContext(AdminReportQueryRequest request, bool supportsProviderModel, bool supportsTool)
    {
        var end = NormalizeDate(request.End) ?? _timeProvider.GetUtcNow().UtcDateTime;
        var start = NormalizeDate(request.Start) ?? end.AddDays(-30);

        if (start >= end)
            throw new ArgumentException("start must be earlier than end.");

        return new AdminReportContext(
            start,
            end,
            NormalizeInterval(request.Interval, start, end),
            new AdminReportAppliedFiltersResponse(
                NormalizeFilter(request.User),
                supportsProviderModel ? NormalizeFilter(request.Provider) : null,
                supportsProviderModel ? NormalizeFilter(request.Model) : null,
                supportsTool ? NormalizeFilter(request.Tool) : null));
    }

    private static AdminReportResponse CreateResponse(
        AdminReportContext context,
        IReadOnlyList<AdminSummaryCardResponse> totals,
        IReadOnlyList<AdminReportSeriesResponse> series,
        IReadOnlyList<AdminBreakdownItemResponse> breakdowns) =>
        new(
            new AdminReportRangeResponse(context.Start, context.End),
            context.Interval,
            totals,
            series,
            breakdowns,
            context.AppliedFilters);

    private static DateTime? NormalizeDate(DateTime? value)
    {
        if (value is null)
            return null;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string? NormalizeFilter(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeInterval(string? value, DateTime start, DateTime end)
    {
        var normalized = NormalizeFilter(value);
        if (normalized is null || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var span = end - start;
            if (span <= TimeSpan.FromDays(45))
                return "day";
            if (span <= TimeSpan.FromDays(180))
                return "week";
            return "month";
        }

        if (string.Equals(normalized, "day", StringComparison.OrdinalIgnoreCase))
            return "day";
        if (string.Equals(normalized, "week", StringComparison.OrdinalIgnoreCase))
            return "week";
        if (string.Equals(normalized, "month", StringComparison.OrdinalIgnoreCase))
            return "month";

        throw new ArgumentException("interval must be one of auto, day, week, or month.");
    }

    private static List<DateTime> GetBuckets(AdminReportContext context)
    {
        var buckets = new List<DateTime>();
        var bucket = FloorToBucket(context.Start, context.Interval);
        var endBucket = FloorToBucket(context.End.AddTicks(-1), context.Interval);

        while (bucket <= endBucket)
        {
            buckets.Add(bucket);
            bucket = AdvanceBucket(bucket, context.Interval);
        }

        return buckets;
    }

    private static DateTime FloorToBucket(DateTime value, string interval) =>
        interval switch
        {
            "day" => new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc),
            "week" => StartOfWeek(value),
            "month" => new DateTime(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };

    private static DateTime AdvanceBucket(DateTime value, string interval) =>
        interval switch
        {
            "day" => value.AddDays(1),
            "week" => value.AddDays(7),
            "month" => value.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };

    private static DateTime StartOfWeek(DateTime value)
    {
        var date = new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    private static AdminReportSeriesResponse BuildSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string key,
        string label,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector,
        Func<T, int> valueSelector)
    {
        var values = rows
            .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)valueSelector(row)));

        return new AdminReportSeriesResponse(
            key,
            label,
            buckets.Select(bucket => new AdminSeriesPointResponse(bucket, values.GetValueOrDefault(bucket))).ToList());
    }

    private static AdminReportSeriesResponse BuildCountSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string key,
        string label,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector)
    {
        var values = rows
            .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
            .ToDictionary(group => group.Key, group => (long)group.Count());

        return new AdminReportSeriesResponse(
            key,
            label,
            buckets.Select(bucket => new AdminSeriesPointResponse(bucket, values.GetValueOrDefault(bucket))).ToList());
    }

    private static AdminReportSeriesResponse BuildFilteredCountSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string key,
        string label,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector,
        Func<T, bool> predicate)
    {
        var values = rows
            .Where(predicate)
            .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
            .ToDictionary(group => group.Key, group => (long)group.Count());

        return new AdminReportSeriesResponse(
            key,
            label,
            buckets.Select(bucket => new AdminSeriesPointResponse(bucket, values.GetValueOrDefault(bucket))).ToList());
    }

    private static AdminReportSeriesResponse BuildAverageSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string key,
        string label,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector,
        Func<T, int> valueSelector)
    {
        var values = rows
            .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
            .ToDictionary(group => group.Key, group => (long)Math.Round(group.Average(valueSelector)));

        return new AdminReportSeriesResponse(
            key,
            label,
            buckets.Select(bucket => new AdminSeriesPointResponse(bucket, values.GetValueOrDefault(bucket))).ToList());
    }

    private static AdminReportSeriesResponse BuildUniqueChatSeries(
        IReadOnlyList<DateTime> buckets,
        string key,
        string label,
        IReadOnlyList<AssistantRunEntity> rows,
        string interval)
    {
        var values = rows
            .GroupBy(row => FloorToBucket(row.CreatedAt, interval))
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => $"{row.Username}:{row.ChatId}").Distinct(StringComparer.Ordinal).LongCount());

        return new AdminReportSeriesResponse(
            key,
            label,
            buckets.Select(bucket => new AdminSeriesPointResponse(bucket, values.GetValueOrDefault(bucket))).ToList());
    }

    private static IReadOnlyList<AdminBreakdownItemResponse> BuildBreakdowns(
        string dimension,
        IEnumerable<KeyValuePair<string, long>> items)
    {
        return items
            .Select(item => new AdminBreakdownItemResponse(dimension, NormalizeBreakdownKey(item.Key), NormalizeBreakdownKey(item.Key), item.Value))
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<AdminReportSeriesResponse> BuildBreakdownValueSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string dimension,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector,
        Func<T, string?> keySelector,
        Func<T, int> valueSelector)
    {
        return rows
            .GroupBy(row => NormalizeBreakdownKey(keySelector(row)))
            .Select(group => new
            {
                Key = group.Key,
                Total = group.Sum(row => (long)valueSelector(row)),
                Values = group
                    .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
                    .ToDictionary(bucket => bucket.Key, bucket => bucket.Sum(row => (long)valueSelector(row)))
            })
            .Where(group => group.Total > 0)
            .OrderByDescending(group => group.Total)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AdminReportSeriesResponse(
                $"breakdown:{dimension}:{group.Key}",
                group.Key,
                buckets.Select(bucket => new AdminSeriesPointResponse(bucket, group.Values.GetValueOrDefault(bucket))).ToList()))
            .ToList();
    }

    private static IReadOnlyList<AdminReportSeriesResponse> BuildBreakdownCountSeries<T>(
        IReadOnlyList<DateTime> buckets,
        string dimension,
        IReadOnlyList<T> rows,
        string interval,
        Func<T, DateTime> timestampSelector,
        Func<T, string?> keySelector)
    {
        return rows
            .GroupBy(row => NormalizeBreakdownKey(keySelector(row)))
            .Select(group => new
            {
                Key = group.Key,
                Total = group.LongCount(),
                Values = group
                    .GroupBy(row => FloorToBucket(timestampSelector(row), interval))
                    .ToDictionary(bucket => bucket.Key, bucket => (long)bucket.Count())
            })
            .Where(group => group.Total > 0)
            .OrderByDescending(group => group.Total)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AdminReportSeriesResponse(
                $"breakdown:{dimension}:{group.Key}",
                group.Key,
                buckets.Select(bucket => new AdminSeriesPointResponse(bucket, group.Values.GetValueOrDefault(bucket))).ToList()))
            .ToList();
    }

    private static bool IsStatus(string? value, string status) =>
        string.Equals(value, status, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBreakdownKey(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "(unknown)" : trimmed;
    }

    private static void AddIfPresent(ISet<string> values, string? value)
    {
        var normalized = NormalizeFilter(value);
        if (!string.IsNullOrWhiteSpace(normalized))
            values.Add(normalized);
    }

    private sealed record AdminReportContext(
        DateTime Start,
        DateTime End,
        string Interval,
        AdminReportAppliedFiltersResponse AppliedFilters);
}
