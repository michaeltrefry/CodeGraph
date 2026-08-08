using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Extractors.ColdFusion;

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
        try
        {
            ct.ThrowIfCancellationRequested();
            var parser = new ColdFusionParser(context, filePath);
            return Task.FromResult(parser.Parse(content));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ColdFusion extraction failed for {FilePath}", filePath);
            return Task.FromResult(ExtractionResult.Failure(ex.Message));
        }
    }
}
