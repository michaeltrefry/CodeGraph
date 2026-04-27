namespace CodeGraph.Services.Prompts;

public sealed record AgentPromptAdminModel(
    string Key,
    string Category,
    string CategoryDisplayName,
    string DisplayName,
    string Description,
    string DefaultText,
    string EffectiveText,
    bool HasOverride,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    int SortOrder);
