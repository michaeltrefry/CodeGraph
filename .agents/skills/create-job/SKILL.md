---
name: create-job
description: Scaffold a new scheduled job following team conventions
allowed-tools: WebFetch, Read, Write, Edit, Grep, Glob, Bash
---

# Create a New Scheduled Job

You are creating a new scheduled job for the CodeGraph project.

## Step 1: Fetch the scheduled jobs convention

Fetch the team's authoritative scheduled jobs pattern from the conventions API:

```
GET http://localhost:5037/api/conventions/scheduled-jobs-convention
```

Use WebFetch to retrieve this. The response JSON has a `content` field with the full convention in markdown. **This is your primary reference for how jobs must be structured.** Follow it precisely.

If the convention API is unavailable, fall back to reading existing jobs in `src/TC.CodeGraphJobs/Jobs/` as reference patterns.

## Step 2: Gather requirements from the user

If not already provided via $ARGUMENTS, ask the user for:
1. **Job name** — what does the job do? (e.g., "CleanupStaleNodes")
2. **What it does** — brief description of the job's responsibility
3. **Args** — any optional arguments the job should accept from `startJob.Args`
4. **Dependencies** — what services does the job need injected?

## Step 3: Create the job class

Create the job in `src/TC.CodeGraphJobs/Jobs/{JobName}Job.cs`.

Key structural requirements:
- Namespace: `TC.CodeGraphJobs.Jobs`
- Inherit from `Job` (from `TC.JobUtilities`)
- Use primary constructor with `ILogger<T>`, `ITcServiceBus`, `Guid instanceKey`, plus any service dependencies
- Call `base(logger, serviceBus, instanceKey)` via the primary constructor
- Override `ExecuteAsync(StartJob startJob)` with the business logic
- Extract any args from `startJob.Args?.TryGetValue("argName", out var value)`
- Add XML doc comment describing the job's purpose and suggested schedule

## Step 4: Add endpoint to JobsController

Every job **must** have a corresponding endpoint in `src/TC.CodeGraphJobs/Controllers/JobsController.cs`. This is how the external scheduler launches the job.

Add a new method following this pattern:

```csharp
/// <summary>
/// {Description of what the job does}.
/// Args: {list of args}
/// </summary>
[HttpPost(nameof({JobName}Job))]
public StartJobResult {JobName}Job([FromBody] StartJob request)
{
    return runner.RunJob<{JobName}Job>(
        new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
}
```

The method name and `nameof()` must match the job class name exactly.

## Step 5: Register in Jobs Startup.cs

Add the job type registration in `src/TC.CodeGraphJobs/Startup.cs` inside the `WithRegistrations` block, alongside the other job registrations:

```csharp
container.RegisterType<{JobName}Job>();
```

Add the required `using` statement if not already present.

## Step 6: Summary

After creating all files, provide a summary of:
- Files created/modified
- The job class name and what it does
- Any args it accepts
- Remind the user that the job must also be registered in the **external scheduler database** with a cron expression — this is not done in code
