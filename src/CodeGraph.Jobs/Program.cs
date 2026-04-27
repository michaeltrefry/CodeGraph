namespace CodeGraph.Jobs;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        Startup.ConfigureServices(builder.Services, builder.Configuration);
        using var host = builder.Build();
        await host.RunAsync();
    }
}
