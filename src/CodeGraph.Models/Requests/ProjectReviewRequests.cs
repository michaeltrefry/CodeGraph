namespace CodeGraph.Models.Requests;

public record StartProjectReviewRequest(string ProjectName, string Mode = "standard");
