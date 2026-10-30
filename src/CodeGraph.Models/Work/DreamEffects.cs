namespace CodeGraph.Models.Work;

/// <summary>Observed correction events, not a causal quality score; the two windows have different lengths.</summary>
public sealed record DreamRecurrence(int Events, int WhileServed, int WhileNotServed);
public sealed record DreamRuleEffect(Guid EntryId, string Summary, DateTime ChangedAt, DreamRecurrence Before, DreamRecurrence After);
public sealed record DreamEffects(IReadOnlyList<DreamRuleEffect> Rules);
