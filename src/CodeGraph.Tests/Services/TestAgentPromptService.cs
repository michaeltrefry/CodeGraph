using CodeGraph.Services.Prompts;

namespace CodeGraph.Tests.Services;

internal sealed class TestAgentPromptService(IReadOnlyDictionary<string, string>? overrides = null) : IAgentPromptService
{
    private readonly Dictionary<string, string> _overrides = overrides is null
        ? new(StringComparer.OrdinalIgnoreCase)
        : new(overrides, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AgentPromptAdminModel>> ListAsync()
        => Task.FromResult<IReadOnlyList<AgentPromptAdminModel>>([]);

    public Task<AgentPromptAdminModel?> GetAsync(string promptKey)
        => Task.FromResult<AgentPromptAdminModel?>(null);

    public Task<AgentPromptAdminModel?> SaveOverrideAsync(string promptKey, string promptText, string updatedBy)
        => Task.FromResult<AgentPromptAdminModel?>(null);

    public Task<bool?> ResetOverrideAsync(string promptKey)
        => Task.FromResult<bool?>(true);

    public Task<string> GetEffectivePromptAsync(string promptKey)
        => _overrides.TryGetValue(promptKey, out var prompt)
            ? Task.FromResult(prompt)
            : throw new InvalidOperationException($"No test prompt override was configured for '{promptKey}'.");
}
