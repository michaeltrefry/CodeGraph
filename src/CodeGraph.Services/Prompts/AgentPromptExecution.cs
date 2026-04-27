using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Prompts;

internal static class AgentPromptExecution
{
    public static async Task<string> GetEffectivePromptOrDefaultAsync(
        IAgentPromptService? promptService,
        string promptKey,
        string defaultPrompt,
        ILogger logger,
        string usage)
    {
        if (promptService is null)
            return defaultPrompt;

        try
        {
            return await promptService.GetEffectivePromptAsync(promptKey);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(
                ex,
                "Falling back to default agent prompt {PromptKey} for {Usage}",
                promptKey,
                usage);
            return defaultPrompt;
        }
    }
}
