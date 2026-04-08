using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Jobs;

public interface IJobScheduleService
{
    Task<IReadOnlyList<JobScheduleResponse>> ListAsync();
    Task<JobScheduleResponse?> GetAsync(long id);
    Task<JobScheduleResponse> CreateAsync(CreateJobScheduleRequest request);
    Task<JobScheduleResponse?> UpdateAsync(long id, UpdateJobScheduleRequest request);
    Task<bool> DeleteAsync(long id);
    Task<JobScheduleResponse?> SetEnabledAsync(long id, bool isEnabled);
    Task<JobExecutionResponse?> RunNowAsync(long id, CancellationToken ct = default);
    Task<bool> TryRunNextDueScheduleAsync(CancellationToken ct = default);
}
