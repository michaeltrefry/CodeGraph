namespace CodeGraph.Jobs;

public class Program
{
    public static async Task Main(string[] args)
    {
        JobsReadinessMarker.Clear();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        Startup.ConfigureServices(builder.Services, builder.Configuration);
        using var host = builder.Build();
        await Startup.InitializeAsync(host.Services);
        JobsReadinessMarker.MarkReady();
        await host.RunAsync();
    }
}
