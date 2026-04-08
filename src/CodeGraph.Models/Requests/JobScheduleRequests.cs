using System.Text.Json;

namespace CodeGraph.Models.Requests;

public class CreateJobScheduleRequest
{
    public string Name { get; set; } = "";
    public string JobType { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string CronExpression { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public JsonElement? Args { get; set; }
}

public class UpdateJobScheduleRequest
{
    public string Name { get; set; } = "";
    public string JobType { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string CronExpression { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public JsonElement? Args { get; set; }
}
