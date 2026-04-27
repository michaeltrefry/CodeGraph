namespace CodeGraph.Models.Responses;

public record AssistantConfigurationResponse(
    string DefaultProvider,
    string DefaultModel,
    IReadOnlyList<AssistantProviderOptionResponse> Providers,
    IndexingConfigurationResponse Indexing);

public record AssistantProviderOptionResponse(
    string Name,
    string DisplayName,
    string DefaultModel,
    IReadOnlyList<string> Models);

public record IndexingConfigurationResponse(
    string Provider,
    string Model);
