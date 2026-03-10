using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.ColdFusion;

public class ColdFusionExtractor : ICodeExtractor
{
    private readonly ILogger<ColdFusionExtractor> _logger;

    public ColdFusionExtractor(ILogger<ColdFusionExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".cfm", ".cfc" };

    public Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        var parser = new ColdFusionParser(context, filePath);
        var result = parser.Parse(content);
        return Task.FromResult(result);
    }
}
