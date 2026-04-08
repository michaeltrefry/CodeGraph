namespace CodeGraph.Jobs;

public class ScheduleRunnerWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduleRunnerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scheduleService = scope.ServiceProvider.GetRequiredService<IJobScheduleService>();
                var ranSchedule = await scheduleService.TryRunNextDueScheduleAsync(stoppingToken);
                if (!ranSchedule)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule runner loop failed");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}
