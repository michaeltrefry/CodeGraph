using System.Net;
using Autofac.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi;

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
        // Add services to the container.
        builder.Host.UseServiceProviderFactory(startup);
        var app = builder.Build();
        startup.Configure(app);
        app.MapMcp();

        // Generate MCP documentation on startup (best-effort)
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var mcpDocService = scope.ServiceProvider.GetRequiredService<IMcpDocService>();
                await mcpDocService.RegenerateAsync();
            }
            catch (Exception ex)
            {
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Failed to regenerate MCP docs on startup");
            }
        });

        app.Run();
    }


        
}
