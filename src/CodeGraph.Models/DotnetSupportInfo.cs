namespace CodeGraph.Models;

public record DotnetSupportInfo(
    string OverallStatus,
    string Summary,
    DotnetSdkSupportInfo? Sdk,
    IReadOnlyList<DotnetTargetFrameworkSupportInfo> TargetFrameworks);

public record DotnetSdkSupportInfo(
    string Version,
    string Channel,
    string DisplayName,
    string SupportStatus,
    DateTime? SupportEndedOn,
    bool IsPinnedByGlobalJson);

public record DotnetTargetFrameworkSupportInfo(
    string Moniker,
    string DisplayName,
    string SupportStatus,
    DateTime? SupportEndedOn);
