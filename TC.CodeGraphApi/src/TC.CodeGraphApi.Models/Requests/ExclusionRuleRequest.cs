namespace TC.CodeGraphApi.Models.Requests;

public class ExclusionRuleRequest
{
    public string TargetType { get; set; } = "";
    public string TargetValue { get; set; } = "";
    public string ExclusionType { get; set; } = "";
    public string? Reason { get; set; }
}

public class UpdateExclusionRuleRequest
{
    public string ExclusionType { get; set; } = "";
    public string? Reason { get; set; }
}
