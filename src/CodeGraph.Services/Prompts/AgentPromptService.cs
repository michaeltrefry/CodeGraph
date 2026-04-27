using CodeGraph.Data;

namespace CodeGraph.Services.Prompts;

public sealed class AgentPromptService(IAdminStore store) : IAgentPromptService
{
    public async Task<IReadOnlyList<AgentPromptAdminModel>> ListAsync()
    {
        var overrides = await store.ListPromptOverridesAsync();
        var overridesByKey = overrides.ToDictionary(o => o.PromptKey, o => o, StringComparer.OrdinalIgnoreCase);

        return AgentPromptCatalog.All
            .OrderBy(p => p.SortOrder)
            .Select(definition => BuildModel(
                definition,
                overridesByKey.TryGetValue(definition.Key, out var entity) ? entity : null))
            .ToList();
    }

    public async Task<AgentPromptAdminModel?> GetAsync(string promptKey)
    {
        if (!AgentPromptCatalog.TryGet(promptKey, out var definition) || definition is null)
            return null;

        var overrideEntity = await store.GetPromptOverrideAsync(definition.Key);
        return BuildModel(definition, overrideEntity);
    }

    public async Task<AgentPromptAdminModel?> SaveOverrideAsync(string promptKey, string promptText, string updatedBy)
    {
        if (!AgentPromptCatalog.TryGet(promptKey, out var definition) || definition is null)
            return null;

        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("Prompt text is required.", nameof(promptText));

        await store.UpsertPromptOverrideAsync(new AgentPromptOverrideEntity
        {
            PromptKey = definition.Key,
            PromptText = promptText.Trim(),
            UpdatedBy = updatedBy,
            UpdatedAt = DateTime.UtcNow
        });

        return await GetAsync(definition.Key);
    }

    public async Task<bool?> ResetOverrideAsync(string promptKey)
    {
        if (!AgentPromptCatalog.TryGet(promptKey, out var definition) || definition is null)
            return null;

        await store.DeletePromptOverrideAsync(definition.Key);
        return true;
    }

    public async Task<string> GetEffectivePromptAsync(string promptKey)
    {
        var prompt = await GetAsync(promptKey)
            ?? throw new InvalidOperationException($"Unknown agent prompt key '{promptKey}'.");
        return prompt.EffectiveText;
    }

    private static AgentPromptAdminModel BuildModel(
        AgentPromptDefinition definition,
        AgentPromptOverrideEntity? overrideEntity)
    {
        var hasOverride = overrideEntity is not null;
        return new AgentPromptAdminModel(
            definition.Key,
            definition.Category,
            definition.CategoryDisplayName,
            definition.DisplayName,
            definition.Description,
            definition.DefaultText,
            hasOverride ? overrideEntity!.PromptText : definition.DefaultText,
            hasOverride,
            overrideEntity?.UpdatedBy,
            overrideEntity?.UpdatedAt,
            definition.SortOrder);
    }
}
