using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public interface ISecurityAnalyzer
{
    Task<SecurityScanResult> ScanAsync(string projectName, string repoPath, CancellationToken ct = default);
}
