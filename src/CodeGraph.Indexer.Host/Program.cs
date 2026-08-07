using System.Net;

namespace CodeGraph.Indexer.Host;

public class Program
{
    public static async Task<int> Main(string[] args)
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

        if (IndexerSecurityBoundaryValidator.IsRequested(args))
        {
            await app.StartAsync();
            try
            {
                return await IndexerSecurityBoundaryValidator.RunAsync(app.Services, args);
            }
            finally
            {
                await app.StopAsync();
            }
        }

        await app.RunAsync();
        return 0;
    }
}
