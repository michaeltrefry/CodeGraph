namespace TC.CodeGraphApi.Models.Responses;

public class ExclusionRuleResponse
{
    public long Id { get; set; }
    public string TargetType { get; set; } = "";
    public string TargetValue { get; set; } = "";
    public string ExclusionType { get; set; } = "";
    public string? Reason { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
