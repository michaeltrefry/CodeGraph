using CodeGraph.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Services.Configuration;

public static class CodeGraphOptionsServiceCollectionExtensions
{
    public const string SectionName = "CodeGraph";

    public static IServiceCollection AddCodeGraphOptions(this IServiceCollection services, IConfiguration configuration)
    {
        var root = configuration.GetSection(SectionName);

        services.AddOptions<CodeGraphServiceSettings>()
            .Bind(root)
            .PostConfigure(CodeGraphSettingsNormalizer.Normalize);

        services.AddOptions<CodeGraphStorageOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.StorageOptions)));

        services.AddOptions<AnalysisOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.AnalysisOptions)));

        services.AddOptions<RepositorySourceOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.RepositorySource)))
            .PostConfigure(CodeGraphSettingsNormalizer.Normalize);

        services.AddOptions<IndexingOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.IndexingOptions)));

        services.AddOptions<ConsumerOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.ConsumerOptions)));

        services.AddOptions<WikiOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.WikiOptions)));

        services.AddOptions<RabbitMqOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.RabbitMqOptions)));

        return services;
    }
}
