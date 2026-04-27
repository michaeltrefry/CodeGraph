namespace CodeGraph.Services.Configuration;

public interface ILlmConfigInvalidator
{
    event Action<string>? ProviderChanged;
    event Action? AnalysisChanged;
    event Action? ReviewChanged;
    event Action? AssistantChanged;

    void InvalidateProvider(string providerKey);
    void InvalidateAnalysis();
    void InvalidateReview();
    void InvalidateAssistant();
}

public sealed class LlmConfigInvalidator : ILlmConfigInvalidator
{
    public event Action<string>? ProviderChanged;
    public event Action? AnalysisChanged;
    public event Action? ReviewChanged;
    public event Action? AssistantChanged;

    public void InvalidateProvider(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ProviderChanged?.Invoke(providerKey.Trim().ToLowerInvariant());
    }

    public void InvalidateAnalysis() => AnalysisChanged?.Invoke();

    public void InvalidateReview() => ReviewChanged?.Invoke();

    public void InvalidateAssistant() => AssistantChanged?.Invoke();
}
