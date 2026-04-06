using System.Net;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Load settings from environment / .env file
        var appSettings = new CodeGraphServiceSettings();
        appSettings.LoadEnvironmentOverrides();

        Startup.ConfigureServices(builder.Services, appSettings);

        builder.WebHost
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Any, Startup.Port);
            });

        var app = builder.Build();
        Startup.Configure(app, appSettings);

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
