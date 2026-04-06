namespace CodeGraph.Jobs.Jobs;

public class StartJob
{
    public Guid Key { get; set; } = Guid.NewGuid();
    public Dictionary<string, string> Args { get; set; } = new();
}

public class StartJobResult
{
    public Guid Key { get; init; }
    public string Status { get; init; } = "started";
}

public interface IJob
{
    Task ExecuteAsync(StartJob startJob, CancellationToken ct = default);
}

public interface IJobRunner
{
    StartJobResult RunJob<T>(StartJob startJob) where T : IJob;
}

public class JobRunner(IServiceProvider serviceProvider, ILogger<JobRunner> logger) : IJobRunner
{
    public StartJobResult RunJob<T>(StartJob startJob) where T : IJob
    {
        var key = startJob.Key;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var job = scope.ServiceProvider.GetRequiredService<T>();
                await job.ExecuteAsync(startJob);
                logger.LogInformation("Job {JobType} ({Key}) completed", typeof(T).Name, key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobType} ({Key}) failed", typeof(T).Name, key);
            }
        });

        return new StartJobResult { Key = key };
    }
}
