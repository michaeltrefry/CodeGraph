namespace CodeGraph.Jobs;

public sealed record JobExecutionResult(
    bool Success,
    string Message,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);
