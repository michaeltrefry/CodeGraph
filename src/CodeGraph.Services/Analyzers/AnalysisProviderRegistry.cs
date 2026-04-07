using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Analyzers;

public class AnalysisProviderRegistry(
    IEnumerable<IAnalysisModelProvider> providers,
    IOptions<AnalysisOptions> optionsAccessor) : IAnalysisProviderRegistry
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private readonly Dictionary<string, IAnalysisModelProvider> _providers =
        providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);

    public IAnalysisModelProvider GetProvider(string? providerName = null)
    {
        var requested = string.IsNullOrWhiteSpace(providerName)
            ? options.DefaultProvider
            : providerName;

        if (_providers.TryGetValue(requested, out var provider))
            return provider;

        var available = _providers.Count == 0
            ? "(none)"
            : string.Join(", ", _providers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException(
            $"Analysis provider '{requested}' is not registered. Available providers: {available}");
    }
}
