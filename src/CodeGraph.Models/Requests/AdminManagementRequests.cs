namespace CodeGraph.Models.Requests;

public record AddAdminRequest(string Username);

public class UpdateAgentPromptRequest
{
    public string PromptText { get; set; } = "";
}

public record CreateDatabaseSourceRequest(
    string ServerName,
    string? DatabaseName,
    string ConnectionString,
    bool? Enabled);

public record UpdateDatabaseSourceRequest(
    string? ServerName,
    string? DatabaseName,
    string? ConnectionString,
    bool? Enabled);
