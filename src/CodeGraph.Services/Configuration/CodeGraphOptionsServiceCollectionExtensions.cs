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

        // This invalidator is intentionally process-local; horizontally scaled hosts need a distributed
        // signal later, while LLM config resolvers will keep a short TTL fallback for external SQL edits.
        services.AddSingleton<ILlmConfigInvalidator, LlmConfigInvalidator>();
        services.AddSingleton<ILlmCatalogValidator, LlmCatalogValidator>();
        services.AddSingleton<IDbBackedLlmProviderConfigResolver, DbBackedLlmProviderConfigResolver>();
        services.AddSingleton<IDbBackedAnalysisSettingsResolver, DbBackedAnalysisSettingsResolver>();
        services.AddSingleton<IDbBackedReviewSettingsResolver, DbBackedReviewSettingsResolver>();
        services.AddSingleton<IDbBackedAssistantSettingsResolver, DbBackedAssistantSettingsResolver>();
        services.AddSingleton<ILlmConfigDeprecationWarningService, LlmConfigDeprecationWarningService>();

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

        services.AddOptions<McpOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.McpOptions)));

        services.AddOptions<AuthOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.AuthOptions)));

        services.AddOptions<AssistantRetentionOptions>()
            .Bind(root.GetSection(nameof(CodeGraphServiceSettings.AssistantRetentionOptions)));

        return services;
    }
}
