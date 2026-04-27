using System.Net;

namespace CodeGraph.Indexer.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        Startup.ConfigureServices(builder.Services, builder.Configuration);

        builder.WebHost
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Any, Startup.Port);
            });

        var app = builder.Build();
        Startup.Configure(app);
        await Startup.InitializeAsync(app.Services);
        await app.RunAsync();
    }
}
