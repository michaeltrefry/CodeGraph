using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.Terraform;

public class TerraformExtractor : ICodeExtractor
{
    private readonly ILogger<TerraformExtractor> _logger;

    public TerraformExtractor(ILogger<TerraformExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".tf", ".tfvars" };

    public Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        var parser = new HclParser(context, filePath, _logger);
        var result = parser.Parse(content);
        return Task.FromResult(result);
    }
}
