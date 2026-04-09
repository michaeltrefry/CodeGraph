using CodeGraph.Models;

namespace CodeGraph.Services.Metadata;

public static class DotnetSupportHealthPolicy
{
    public const double OutOfSupportPenalty = 2.5;
    public const double MixedSupportPenalty = 1.0;

    public static double GetPenalty(DotnetSupportInfo? support) =>
        support?.OverallStatus switch
        {
            "out_of_support" => OutOfSupportPenalty,
            "mixed" => MixedSupportPenalty,
            _ => 0
        };

    public static double ApplyPenalty(double baseHealth, DotnetSupportInfo? support)
    {
        var penalty = GetPenalty(support);
        return Math.Round(Math.Clamp(baseHealth - penalty, 1.0, 10.0), 1);
    }
}
