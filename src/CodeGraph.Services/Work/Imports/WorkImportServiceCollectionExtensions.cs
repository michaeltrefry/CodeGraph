using CodeGraph.Models.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Services.Work.Imports;

public static class WorkImportServiceCollectionExtensions
{
    /// <summary>The imports (sc-27876), shared by the API (REST trigger) and the Jobs host (WorkImport job). Needs the MariaDB work store.</summary>
    public static IServiceCollection AddCodeGraphWorkImports(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkImportOptions>(configuration.GetSection(WorkImportOptions.Section));
        services.AddHttpClient(ShortcutRestSource.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
            // Shortcut file URLs redirect to signed storage URLs; the token header must not follow them off Shortcut.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(KanbanRestSource.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<IWorkAttachmentStorage, WorkAttachmentStorage>();
        services.AddTransient<IWorkImportSourceFactory, RestWorkImportSourceFactory>();
        services.AddTransient<ShortcutImporter>();
        services.AddTransient<KanbanImporter>();
        services.AddTransient<WorkImportService>();
        services.AddTransient<IWorkImportRunner>(provider => provider.GetRequiredService<WorkImportService>());
        return services;
    }
}
