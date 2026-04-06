# Scheduled Jobs Convention

Background jobs run on a schedule defined externally, not in the code itself.

## Job Structure

Job classes live in the `TC.RepoNameJobs` project (note: drops "Api" from the name):

```
TC.OrdersApi/           # API host
TC.OrdersJobs/          # Jobs project
```

## Job Class Pattern

```csharp
public class CleanupExpiredOrdersJob
{
    private readonly IOrderRepository _repo;
    private readonly ILogger<CleanupExpiredOrdersJob> _logger;

    public CleanupExpiredOrdersJob(IOrderRepository repo, ILogger<CleanupExpiredOrdersJob> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task Execute()
    {
        // Job logic here
    }
}
```

## Scheduling

- Schedules are stored in an **external scheduler database**, not in code
- Cron expressions define when jobs run
- The scheduler invokes job classes by name
- This means you cannot determine a job's schedule from the code alone

## Identifying Jobs

- Look in `TC.*Jobs` projects
- Job classes typically have an `Execute` method
- They use constructor injection like any other service class
