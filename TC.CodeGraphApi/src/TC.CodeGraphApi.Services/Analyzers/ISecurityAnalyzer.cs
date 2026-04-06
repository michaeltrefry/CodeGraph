using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Analyzers;

public interface ISecurityAnalyzer
{
    Task<SecurityScanResult> ScanAsync(string projectName, string repoPath, CancellationToken ct = default);
}
