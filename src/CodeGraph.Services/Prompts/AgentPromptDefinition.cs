namespace CodeGraph.Services.Prompts;

public sealed record AgentPromptDefinition(
    string Key,
    string Category,
    string CategoryDisplayName,
    string DisplayName,
    string Description,
    string DefaultText,
    int SortOrder);
