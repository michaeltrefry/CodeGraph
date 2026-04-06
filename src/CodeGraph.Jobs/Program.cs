using System.Net;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Jobs;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var appSettings = new CodeGraphServiceSettings();
        builder.Configuration.GetSection("CodeGraph").Bind(appSettings);

        Startup.ConfigureServices(builder.Services, appSettings);

        builder.WebHost
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Any, Startup.Port);
            });

        var app = builder.Build();
        Startup.Configure(app);
        app.Run();
    }
}
