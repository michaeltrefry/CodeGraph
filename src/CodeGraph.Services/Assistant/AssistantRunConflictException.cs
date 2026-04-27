using CodeGraph.Data;

namespace CodeGraph.Services.Assistant;

public sealed class AssistantRunConflictException(AssistantRunConflict conflict) : Exception(conflict.Message)
{
    public AssistantRunConflict Conflict { get; } = conflict;
}
