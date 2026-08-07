using CodeGraph.Data;
using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;

namespace CodeGraph.Jobs.Tests.Jobs;

internal sealed class RecordingBatchAnalysisService : IBatchAnalysisService
{
    public string? ProcessedRepo { get; private set; }
    public int ProcessCompletedCalls { get; private set; }

    public Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null, bool includeAllSource = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default)
    {
        ProcessedRepo = repo;
        ProcessCompletedCalls++;
        return Task.CompletedTask;
    }

    public Task ProcessCompletedBatchAsync(string repoName, string providerBatchId, CancellationToken ct = default)
    {
        ProcessedRepo = repoName;
        ProcessCompletedCalls++;
        return Task.CompletedTask;
    }

    public Task SynthesizeRepoSummaryAsync(string repoName, string batchId, CancellationToken ct) => Task.CompletedTask;

    public Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class RecordingAdminService : IAdminService
{
    public DiscoverRequest? LastDiscoverRequest { get; private set; }
    public DiscoverResponse NextDiscoverResponse { get; set; } = new(0, 0, 0, 0, 0, []);
    public int ReIndexAllCalls { get; private set; }
    public int DetectCommunitiesCalls { get; private set; }
    public int LinkAndDetectCalls { get; private set; }

    public Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request)
    {
        LastDiscoverRequest = request;
        return Task.FromResult(NextDiscoverResponse);
    }

    public Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request) => throw new NotSupportedException();

    public Task<ProcessReposResponse> ReIndexAllAsync()
    {
        ReIndexAllCalls++;
        return Task.FromResult(new ProcessReposResponse([], 0));
    }

    public Task LinkAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task DetectCommunitiesAsync(CancellationToken ct)
    {
        DetectCommunitiesCalls++;
        return Task.CompletedTask;
    }

    public Task LinkAndDetectAsync(CancellationToken ct)
    {
        LinkAndDetectCalls++;
        return Task.CompletedTask;
    }

    public Task ProcessBatchAnalysisAsync(string? repo) => throw new NotSupportedException();
}

internal sealed class RecordingIndexerClient : IIndexerClient
{
    public DiscoverRequest? LastDiscoverRequest { get; private set; }
    public string? LastBatchRepo { get; private set; }
    public int ReIndexAllCalls { get; private set; }
    public int DetectCommunitiesCalls { get; private set; }
    public int LinkAndDetectCalls { get; private set; }
    public int ProcessBatchAnalysisCalls { get; private set; }
    public IndexerAcceptedResponse NextAcceptedResponse { get; set; } =
        new("queued", "Queued indexer run.", 99, "/api/indexer/runs/99");

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
        CancellationToken ct = default)
        => Task.FromResult(NextAcceptedResponse);

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
    {
        ReIndexAllCalls++;
        return Task.FromResult(NextAcceptedResponse);
    }

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request = null,
        CancellationToken ct = default)
    {
        LastDiscoverRequest = request;
        return Task.FromResult(NextAcceptedResponse);
    }

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        => Task.FromResult(NextAcceptedResponse);

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => Task.FromResult(NextAcceptedResponse);

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => Task.FromResult(NextAcceptedResponse);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
    {
        DetectCommunitiesCalls++;
        return Task.FromResult(NextAcceptedResponse);
    }

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
    {
        LinkAndDetectCalls++;
        return Task.FromResult(NextAcceptedResponse);
    }

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo = null,
        CancellationToken ct = default)
    {
        LastBatchRepo = repo;
        ProcessBatchAnalysisCalls++;
        return Task.FromResult(NextAcceptedResponse);
    }

    public Task<IndexerRunResponse?> GetRunAsync(string username, long runId, CancellationToken ct = default)
        => Task.FromResult<IndexerRunResponse?>(null);

    public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string username,
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IndexerRunResponse>>([]);
}

internal sealed class RecordingMcpDocService : IMcpDocService
{
    public int Calls { get; private set; }

    public Task RegenerateAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAssistantRetentionCleanupService : IAssistantRetentionCleanupService
{
    public int Calls { get; private set; }
    public AssistantRetentionCleanupResult Result { get; set; } = new(1, 2, 3, 4, 5, 6);

    public Task<AssistantRetentionCleanupResult> CleanupAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class InMemoryJobScheduleStore : IJobScheduleStore
{
    private readonly List<JobScheduleEntity> _items = [];
    private readonly object _sync = new();
    private long _nextId = 1;
    public int RenewalCount { get; private set; }

    public Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync() =>
        Task.FromResult<IReadOnlyList<JobScheduleEntity>>(_items.OrderBy(x => x.Name).ToList());

    public Task<JobScheduleEntity?> GetScheduleByIdAsync(long id) =>
        Task.FromResult(_items.FirstOrDefault(x => x.Id == id)?.Clone());

    public Task<JobScheduleEntity?> GetScheduleByNameAsync(string name) =>
        Task.FromResult(_items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Clone());

    public Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity)
    {
        var clone = entity.Clone();
        clone.Id = _nextId++;
        _items.Add(clone);
        return Task.FromResult(clone.Clone());
    }

    public Task<bool> UpdateScheduleAsync(JobScheduleEntity entity)
    {
        lock (_sync)
        {
            var existing = _items.FirstOrDefault(x =>
                x.Id == entity.Id && x.ScheduleRevision == entity.ScheduleRevision);
            if (existing is null)
                return Task.FromResult(false);

            existing.Name = entity.Name;
            existing.JobType = entity.JobType;
            existing.IsEnabled = entity.IsEnabled;
            existing.CronExpression = entity.CronExpression;
            existing.TimeZoneId = entity.TimeZoneId;
            existing.ArgsJson = entity.ArgsJson;
            existing.NextRunUtc = entity.NextRunUtc;
            existing.ScheduleRevision++;
            existing.UpdatedAtUtc = entity.UpdatedAtUtc;
            return Task.FromResult(true);
        }
    }

    public Task DeleteScheduleAsync(long id)
    {
        _items.RemoveAll(x => x.Id == id);
        return Task.CompletedTask;
    }

    public Task<JobScheduleEntity?> TryAcquireDueScheduleAsync(DateTime utcNow, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var schedule = _items
            .Where(x => x.IsEnabled && x.NextRunUtc <= utcNow && (x.LeaseExpiresUtc is null || x.LeaseExpiresUtc <= utcNow))
            .OrderBy(x => x.NextRunUtc)
            .FirstOrDefault();

        return Task.FromResult(Acquire(schedule, utcNow, leaseOwner, leaseDuration));
    }

    public Task<JobScheduleEntity?> TryAcquireScheduleAsync(long id, DateTime utcNow, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var schedule = _items.FirstOrDefault(x => x.Id == id);
        return Task.FromResult(Acquire(schedule, utcNow, leaseOwner, leaseDuration));
    }

    public Task<bool> RenewLeaseAsync(
        long id,
        DateTime utcNow,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            var schedule = _items.FirstOrDefault(x =>
                x.Id == id
                && x.LeaseOwner == leaseToken
                && x.LeaseExpiresUtc > utcNow);
            if (schedule is null)
                return Task.FromResult(false);

            schedule.LeaseExpiresUtc = utcNow.Add(leaseDuration);
            schedule.UpdatedAtUtc = utcNow;
            RenewalCount++;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkRunStartedAsync(
        long id,
        DateTime startedAtUtc,
        DateTime fenceCheckedAtUtc,
        string leaseToken,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            var schedule = _items.FirstOrDefault(x =>
                x.Id == id
                && x.LeaseOwner == leaseToken
                && x.LeaseExpiresUtc > fenceCheckedAtUtc);
            if (schedule is null)
                return Task.FromResult(false);

            schedule.LastRunStartedUtc = startedAtUtc;
            schedule.LastRunStatus = "running";
            schedule.LastError = null;
            schedule.UpdatedAtUtc = startedAtUtc;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkRunCompletedAsync(
        long id,
        DateTime completedAtUtc,
        DateTime? nextRunUtc,
        string status,
        string? error,
        DateTime fenceCheckedAtUtc,
        long acquiredScheduleRevision,
        string leaseToken,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            var schedule = _items.FirstOrDefault(x =>
                x.Id == id
                && x.LeaseOwner == leaseToken
                && x.LeaseExpiresUtc > fenceCheckedAtUtc);
            if (schedule is null)
                return Task.FromResult(false);

            schedule.LastRunCompletedUtc = completedAtUtc;
            schedule.LastRunStatus = status;
            schedule.LastError = error;
            if (nextRunUtc.HasValue && schedule.ScheduleRevision == acquiredScheduleRevision)
                schedule.NextRunUtc = nextRunUtc.Value;
            schedule.ScheduleRevision++;
            schedule.LeaseAcquiredUtc = null;
            schedule.LeaseOwner = null;
            schedule.LeaseExpiresUtc = null;
            schedule.UpdatedAtUtc = completedAtUtc;
            return Task.FromResult(true);
        }
    }

    public void ReplaceLease(long id, string leaseToken, DateTime expiresUtc)
    {
        lock (_sync)
        {
            var schedule = _items.First(x => x.Id == id);
            schedule.LeaseOwner = leaseToken;
            schedule.LeaseExpiresUtc = expiresUtc;
        }
    }

    private static JobScheduleEntity? Acquire(JobScheduleEntity? schedule, DateTime utcNow, string leaseOwner, TimeSpan leaseDuration)
    {
        if (schedule is null)
            return null;
        if (schedule.LeaseExpiresUtc is not null && schedule.LeaseExpiresUtc > utcNow)
            return null;

        schedule.LeaseOwner = leaseOwner;
        schedule.LeaseAcquiredUtc = utcNow;
        schedule.LeaseExpiresUtc = utcNow.Add(leaseDuration);
        schedule.UpdatedAtUtc = utcNow;
        return schedule.Clone();
    }
}

internal static class JobScheduleEntityCloneExtensions
{
    public static JobScheduleEntity Clone(this JobScheduleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        JobType = entity.JobType,
        IsEnabled = entity.IsEnabled,
        CronExpression = entity.CronExpression,
        TimeZoneId = entity.TimeZoneId,
        ArgsJson = entity.ArgsJson,
        NextRunUtc = entity.NextRunUtc,
        ScheduleRevision = entity.ScheduleRevision,
        LastRunStartedUtc = entity.LastRunStartedUtc,
        LastRunCompletedUtc = entity.LastRunCompletedUtc,
        LastRunStatus = entity.LastRunStatus,
        LastError = entity.LastError,
        LeaseAcquiredUtc = entity.LeaseAcquiredUtc,
        LeaseOwner = entity.LeaseOwner,
        LeaseExpiresUtc = entity.LeaseExpiresUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        UpdatedAtUtc = entity.UpdatedAtUtc
    };
}
