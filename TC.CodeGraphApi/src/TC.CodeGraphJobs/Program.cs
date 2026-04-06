using System.Net;
using Autofac.Extensions.DependencyInjection;

namespace TC.CodeGraphJobs;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        var startup = new Startup(builder.Environment);

        builder.WebHost
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Any, Startup.Port);
            });

        builder.Host.UseServiceProviderFactory(startup);
        var app = builder.Build();
        startup.Configure(app);
        app.Run();
    }
}
