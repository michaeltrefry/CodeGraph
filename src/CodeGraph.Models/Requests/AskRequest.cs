namespace CodeGraph.Models.Requests;

public record AskRequest(
    string Question,
    string? Context = null,
    List<ChatMessage>? History = null,
    string? Provider = null,
    string? Model = null,
    string? ChatId = null);

public record ChatMessage(string Role, string Content);
